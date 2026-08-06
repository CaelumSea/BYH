using System.Runtime.InteropServices;

namespace SelectionAssistant.Platform.Windows.Speech;

/// <summary>
/// Plays in-memory MP3 bytes via Win32 MCI (<c>winmm.dll!mciSendString</c>).
/// The flow: write bytes to a temp <c>.mp3</c> → <c>open</c> an MCI alias →
/// <c>play ... wait</c> (blocks the calling thread until playback finishes) →
/// <c>close</c> → delete the temp file.
/// <para>
/// <b>Why MCI and not NAudio / Avalonia.Media.MediaPlayer</b>: this app is
/// PublishAot=true + TrimMode=full. NAudio relies on reflection + COM interop
/// and breaks under trimming; MCI is a plain C string ABI, fully AOT-safe. The
/// project already uses the same <c>[LibraryImport]</c> pattern for clipboard /
/// hooks / hotkeys.
/// </para>
/// <para>
/// <b>Cancellation</b>: <c>play ... wait</c> blocks the thread it's called on.
/// <see cref="PlayMp3Bytes"/> is meant to run on a background <c>Task.Run</c>.
/// The caller passes a <see cref="CancellationToken"/>; cancelling issues
/// <c>stop</c> + <c>close</c> on the active alias, which unblocks the wait.
/// Re-entering <see cref="PlayMp3Bytes"/> (user clicks Speak again) stops the
/// prior playback first — "click again = restart" semantics.
/// </para>
/// </summary>
public static partial class MciAudioPlayer
{
    // Aliases are process-unique so concurrent openers don't collide.
    private static int _aliasSeed;
    private static string? _activeAlias;
    private static readonly object _gate = new();

    /// <summary>
    /// Plays <paramref name="mp3"/> synchronously. Blocks until playback finishes
    /// OR the <paramref name="cancellationToken"/> is cancelled. Caller should
    /// run this on a background thread (e.g. <c>Task.Run</c>) to avoid blocking
    /// the UI. Cancelling stops playback promptly.
    /// </summary>
    /// <exception cref="InvalidOperationException">MCI open/play failed (driver missing, codec unavailable).</exception>
    public static void PlayMp3Bytes(byte[] mp3, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mp3);
        if (mp3.Length == 0)
        {
            return;
        }

        // Stop any currently-playing alias before starting a new one. This gives
        // the "click Speak again = restart" behaviour and avoids MCI's "device
        // already open" error.
        Stop();

        string tempPath = Path.Combine(Path.GetTempPath(), $"byh-tts-{Guid.NewGuid():N}.mp3");
        string alias = $"byhtts{Interlocked.Increment(ref _aliasSeed)}";

        try
        {
            File.WriteAllBytes(tempPath, mp3);

            lock (_gate)
            {
                _activeAlias = alias;
            }

            // open: MPEGVideo is the MCI driver selector for mp3 files on all
            // modern Windows. Quoting the path protects against spaces / Unicode
            // in the temp path. Failure → MCIERROR string in retBuf.
            ThrowIfMciError(Send($"open \"{tempPath}\" type mpegvideo alias {alias}"),
                $"打开音频设备失败（MCI open）。");

            // Register cancellation to abort playback: once the alias is opened,
            // a `stop` + `close` will unblock the pending `play wait` below.
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => StopAlias(alias));

            // play ... wait blocks this thread until the clip finishes naturally.
            // If the token fires, StopAlias issues stop+close which makes this
            // return promptly with a non-zero (but benign) error code.
            Send($"play {alias} wait");
        }
        finally
        {
            // Always close + delete. close is idempotent if already closed by
            // the cancellation callback; ignore its error code.
            Send($"close {alias}");

            lock (_gate)
            {
                if (_activeAlias == alias)
                {
                    _activeAlias = null;
                }
            }

            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>
    /// Stops any active playback started by <see cref="PlayMp3Bytes"/>. Safe to
    /// call when nothing is playing (no-op). Used by SelectionRuntime on
    /// shutdown / toolbar dismiss to guarantee audio doesn't outlive the app.
    /// </summary>
    public static void Stop()
    {
        string? alias;
        lock (_gate)
        {
            alias = _activeAlias;
            _activeAlias = null;
        }
        if (alias is not null)
        {
            StopAlias(alias);
        }
    }

    private static void StopAlias(string alias)
    {
        // stop unblocks a pending `play ... wait` on this alias; close releases
        // the device. Both are best-effort — if the alias was already closed
        // (e.g. playback finished naturally), MCI returns an error code that we
        // intentionally ignore.
        Send($"stop {alias}");
        Send($"close {alias}");
    }

    private static void ThrowIfMciError(int errorCode, string userMessage)
    {
        // mciSendString returns 0 on success; non-zero means an error (the
        // textual form is in the return buffer of the corresponding call). The
        // `play wait` path intentionally ignores errors (cancellation also
        // surfaces as a non-zero code), so only `open` uses this helper.
        if (errorCode != 0)
        {
            throw new InvalidOperationException(userMessage);
        }
    }

    private static int Send(string command)
    {
        // We discard the textual return buffer: error text is never surfaced
        // (Chinese status messages come from SelectionRuntime/TtsException), and
        // LibraryImport's source generator does not support StringBuilder. Pass
        // a null destination + zero length — MCI treats that as "no buffer".
        return mciSendString(command, lpstrReturnString: nint.Zero, uReturnLength: 0, hwndCallback: nint.Zero);
    }

    // CRITICAL: LibraryImport defaults to searching for the exact method name
    // as the entry point ("mciSendString"). winmm.dll exports ANSI + W variants
    // (mciSendStringA / mciSendStringW) but NOT an undecorated "mciSendString",
    // so a plain LibraryImport with Utf16 strings throws EntryPointNotFoundException
    // at first call. Explicitly target mciSendStringW to match the Utf16 marshalling.
    [LibraryImport("winmm.dll", EntryPoint = "mciSendStringW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int mciSendString(
        string lpstrCommand,
        nint lpstrReturnString,
        int uReturnLength,
        nint hwndCallback);
}
