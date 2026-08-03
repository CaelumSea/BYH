using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Clipboard;

/// <summary>
/// Win32 clipboard access with bounded OpenClipboard retries and a dedicated
/// message-only window for WM_CLIPBOARDUPDATE. Only safely materialized formats
/// are included in snapshots.
/// </summary>
public sealed unsafe partial class Win32Clipboard : IClipboardAccess, IDisposable
{
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const int MaxTextBytes = 8 * 1024 * 1024;
    private const int MaxDibBytes = 32 * 1024 * 1024;
    private const int MaxFileCount = 4_096;
    private const int MaxPathChars = 32_768;

    private static readonly nint HwndMessage = new(-3);
    private static readonly ConcurrentDictionary<nint, WeakReference<Win32Clipboard>> Instances = new();
    private static readonly WindowProcedure SharedWindowProcedure = WindowProc;

    private readonly object _clipboardGate = new();
    private readonly object _subscriptionGate = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _messageThread;
    private readonly TimeSpan _openTimeout;
    private readonly string _windowClassName = $"BYH.Clipboard.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private Action? _changeCallback;
    private Exception? _startupFailure;
    private nint _windowHandle;
    private ushort _windowClassAtom;
    private int _disposed;

    public Win32Clipboard(TimeSpan? openTimeout = null)
    {
        _openTimeout = openTimeout ?? TimeSpan.FromMilliseconds(200);
        if (_openTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(openTimeout));
        }

        _messageThread = new Thread(MessageThreadMain)
        {
            IsBackground = true,
            Name = "BYH.ClipboardMessages",
            Priority = ThreadPriority.Normal,
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(3)))
        {
            throw new TimeoutException("Clipboard message window startup timed out.");
        }

        if (_startupFailure is not null)
        {
            throw new InvalidOperationException("Clipboard message window startup failed.", _startupFailure);
        }
    }

    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public uint? GetOwnerProcessId()
    {
        nint owner = GetClipboardOwner();
        if (owner == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(owner, out uint processId);
        return processId == 0 ? null : processId;
    }

    public ClipboardSnapshot Backup()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        uint fallbackSequence = GetSequenceNumber();

        return TryWithOpenClipboard(
            () =>
            {
                uint sequence = GetClipboardSequenceNumber();
                bool wasEmpty = EnumClipboardFormats(0) == 0;
                string? text = IsClipboardFormatAvailable(CfUnicodeText)
                    ? TryReadUnicodeText(MaxTextBytes)
                    : null;
                // P2 memory: rent the CF_DIB buffer from ArrayPool instead of
                // `new byte[]`. A screen-shot CF_DIB can be up to MaxDibBytes
                // (32 MB); the selection-capture path calls Backup() on every
                // Ctrl+Insert/Ctrl+C probe (logs show ~9 probes/30s bursts), and
                // each `new byte[]` lands on the NativeAOT large-object heap,
                // which does NOT compact and does NOT return committed memory to
                // the OS — so private bytes climbed to 660 MB on an otherwise
                // idle process. Renting keeps the same buffer circulating in the
                // pool across probes. The rented buffer is owned by the returned
                // snapshot; Restore() copies the first dibLen bytes into a fresh
                // HGLOBAL, after which the snapshot must be Dispose()d by
                // Win32ClipboardCapture to return the buffer.
                // Privacy: Return(..., clearArray: true) zeroes the buffer before
                // it re-enters the pool — without this, stale clipboard screenshot
                // bytes could be read by the next borrower that over-reads past
                // its logical length. Clearing costs ~one memset of dibLen (the
                // same data we just copied), negligible vs. the GDI read.
                (byte[]? dib, int dibLen) = IsClipboardFormatAvailable(CfDib)
                    ? TryReadGlobalBytesPooled(CfDib, MaxDibBytes)
                    : (null, 0);
                string[]? files = IsClipboardFormatAvailable(CfHDrop)
                    ? TryReadFiles()
                    : null;

                return new ClipboardSnapshot(
                    sequenceNumber: sequence,
                    text: text,
                    imageDib: dib,
                    imageDibLength: dibLen,
                    files: files,
                    backupSucceeded: true,
                    wasEmpty: wasEmpty,
                    disposeHook: dib is null
                        ? null
                        : () => ArrayPool<byte>.Shared.Return(dib, clearArray: true));
            },
            out ClipboardSnapshot? snapshot)
            ? snapshot!
            : ClipboardSnapshot.Unavailable(fallbackSequence);
    }

    public string? GetText()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return TryWithOpenClipboard(
            () => IsClipboardFormatAvailable(CfUnicodeText)
                ? TryReadUnicodeText(MaxTextBytes)
                : null,
            out string? text)
            ? text
            : null;
    }

    /// <summary>
    /// R54 v2: reads the current clipboard image as a raw <c>CF_DIB</c> byte
    /// payload (BITMAPINFOHEADER + pixels), or null when the clipboard holds no
    /// image or the payload exceeds <see cref="MaxDibBytes"/>. Mirrors
    /// <see cref="GetText"/>: bounded open retry, size-capped read. The caller
    /// (<c>DibToPngConverter</c>) turns the DIB into a PNG for disk storage.
    /// </summary>
    public byte[]? GetImageDib()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return TryWithOpenClipboard(
            () => IsClipboardFormatAvailable(CfDib)
                ? TryReadGlobalBytes(CfDib, MaxDibBytes)
                : null,
            out byte[]? dib)
            ? dib
            : null;
    }

    /// <summary>P2 memory: ArrayPool-backed variant of <see cref="GetImageDib"/>.
    /// Returns a pooled payload whose <see cref="ImageDibPayload.Buffer"/> may be
    /// larger than <see cref="ImageDibPayload.Length"/> (ArrayPool rounds up to a
    /// bucket); the caller must read only <see cref="ImageDibPayload.Length"/>
    /// bytes and <c>Dispose</c> the payload (ideally via <c>using</c>) so the
    /// buffer returns to the pool instead of landing on the NativeAOT LOH. The
    /// clipboard-history image path (<c>ClipboardHistoryService.TryCaptureImage</c>)
    /// fires on every clipboard image change; pooling the up-to-32 MB CF_DIB read
    /// there mirrors the Backup() fix and closes the second LOH-churn source.
    /// Returns an empty payload (<see cref="ImageDibPayload.IsEmpty"/> true) when
    /// the clipboard holds no image or the read fails.</summary>
    public ImageDibPayload GetImageDibPooled()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        bool ok = TryWithOpenClipboard(
            () =>
            {
                if (!IsClipboardFormatAvailable(CfDib))
                {
                    return (null, 0);
                }
                return TryReadGlobalBytesPooled(CfDib, MaxDibBytes);
            },
            out (byte[]? Buffer, int Length) result);
        if (!ok || result.Buffer is null || result.Length == 0)
        {
            return ImageDibPayload.Empty;
        }
        // Capture the rented buffer into a local so the dispose closure can't
        // be confused by later reassignment; clearArray:true zeroes the bytes
        // (privacy: stale clipboard screenshot must not leak to the next
        // borrower that over-reads past Length).
        byte[] buffer = result.Buffer;
        return new ImageDibPayload(buffer, result.Length,
            () => ArrayPool<byte>.Shared.Return(buffer, clearArray: true));
    }

    public bool Restore(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!snapshot.BackupSucceeded || !snapshot.HasRestorableData)
        {
            return false;
        }

        List<OwnedClipboardMemory> allocations = CreateSnapshotMemory(snapshot);
        if (allocations.Count == 0)
        {
            return false;
        }

        try
        {
            if (GetSequenceNumber() != expectedSequence)
            {
                return false;
            }

            // Audit H10: pre-flight check. EmptyClipboard() wipes the current
            // clipboard contents AND transfers ownership to us; if we then fail
            // every SetClipboardData, the prior contents are gone with nothing
            // replacing them (data loss). The dominant cause of SetClipboardData
            // failure is an invalid (zero) handle — typically because
            // AllocateGlobal returned NULL under memory pressure. Verify every
            // allocation has a live handle BEFORE clearing. (SetClipboardData can
            // still fail post-clear for other reasons — AV interference, another
            // app grabbing the clipboard between our EmptyClipboard and our
            // SetClipboardData — but those races are inherent to the Win32
            // clipboard design and far rarer than a zero-handle allocation.)
            foreach (OwnedClipboardMemory allocation in allocations)
            {
                if (allocation.Handle == IntPtr.Zero)
                {
                    return false;
                }
            }

            return TryWithOpenClipboard(
                () =>
                {
                    if (GetClipboardSequenceNumber() != expectedSequence || !EmptyClipboard())
                    {
                        return false;
                    }

                    bool restoredAny = false;
                    foreach (OwnedClipboardMemory allocation in allocations)
                    {
                        if (SetClipboardData(allocation.Format, allocation.Handle) != 0)
                        {
                            allocation.TransferOwnership();
                            restoredAny = true;
                        }
                    }

                    return restoredAny;
                },
                out bool restored) && restored;
        }
        finally
        {
            foreach (OwnedClipboardMemory allocation in allocations)
            {
                allocation.Dispose();
            }
        }
    }

    public bool Clear(uint expectedSequence)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (GetSequenceNumber() != expectedSequence)
        {
            return false;
        }

        return TryWithOpenClipboard(
            () => GetClipboardSequenceNumber() == expectedSequence && EmptyClipboard(),
            out bool cleared) && cleared;
    }

    /// <summary>
    /// R40 Ocean Eyes: writes the given PNG bytes to the clipboard under the
    /// registered "PNG" format (Windows 10 1809+ and all modern image editors /
    /// chat clients read it). The raw PNG is placed verbatim — no DIB
    /// conversion (PNG → BITMAPINFOHEADER would force an alpha-premultiplied
    /// BGRA copy and is fragile under NativeAOT). Apps that don't understand
    /// CF_PNG will see an empty image; the file on disk (saved by the caller)
    /// is the authoritative artifact.
    /// </summary>
    /// <remarks>
    /// Empties the clipboard first so a stale image from a previous copy
    /// doesn't blend with the new one. Failure to open the clipboard (rare —
    /// another app holds it) returns false; the caller logs but doesn't throw.
    /// </remarks>
    public bool SetPng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0 || png.Length > MaxDibBytes)
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        uint pngFormat = RegisterClipboardFormatW("PNG");
        if (pngFormat == 0)
        {
            return false;
        }

        nint memory = AllocateGlobal(png);
        bool transferred = false;
        try
        {
            bool result = TryWithOpenClipboard(
                () =>
                {
                    if (!EmptyClipboard())
                    {
                        return false;
                    }
                    transferred = SetClipboardData(pngFormat, memory) != 0;
                    return transferred;
                },
                out bool placed) && placed;
            return result;
        }
        finally
        {
            // Ownership is determined by the return value of SetClipboardData,
            // not by a later IsClipboardFormatAvailable query. A clipboard
            // listener may replace the contents between CloseClipboard and the
            // old query; querying then could report "not available" even though
            // Windows already owns (and may already have freed) this HGLOBAL,
            // leading to a double GlobalFree and a delayed access violation.
            if (memory != 0 && !transferred)
            {
                GlobalFree(memory);
            }
        }
    }

    /// <summary>
    /// R54 v2: writes a raw <c>CF_DIB</c> payload (BITMAPINFOHEADER + pixels,
    /// exactly what <see cref="GetImageDib"/> returns) to the clipboard. CF_DIB
    /// (format 8) is the universally-recognized Windows image format — Word,
    /// Paint, chat clients, and every image editor paste from it. Used by
    /// clipboard-history image paste-back (we store the original DIB captured
    /// at copy time, so paste restores the exact same bytes the source app put
    /// up — no PNG→DIB re-encode that could lose alpha or change dimensions).
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="SetPng"/>: bounded open retry, empties first, allocates
    /// a movable HGLOBAL, hands ownership to the system on success. Returns false
    /// on empty input, size cap, or open/set failure.
    /// </remarks>
    public bool SetImageDib(byte[] dib)
    {
        ArgumentNullException.ThrowIfNull(dib);
        if (dib.Length == 0 || dib.Length > MaxDibBytes)
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        nint memory = AllocateGlobal(dib);
        bool transferred = false;
        try
        {
            bool result = TryWithOpenClipboard(
                () =>
                {
                    if (!EmptyClipboard())
                    {
                        return false;
                    }
                    transferred = SetClipboardData(CfDib, memory) != 0;
                    return transferred;
                },
                out bool placed) && placed;
            return result;
        }
        finally
        {
            if (memory != 0 && !transferred)
            {
                GlobalFree(memory);
            }
        }
    }

    /// <summary>
    /// R54 v2 bug fix: writes BOTH a raw <c>CF_DIB</c> payload and the registered
    /// <c>CF_PNG</c> format in a single atomic <see cref="EmptyClipboard"/> +
    /// <see cref="SetClipboardData"/> pair. Ocean Eyes screenshots are copied here
    /// so that every image consumer sees the image:
    /// <list type="bullet">
    ///   <item>Word / Paint / older chat clients read <c>CF_DIB</c> (format 8).</item>
    ///   <item>Modern editors (VS Code, some browsers, image-aware chat) read
    ///   <c>CF_PNG</c> (registered format, preserves alpha + exact bytes).</item>
    ///   <item>BYH's own clipboard history reads <c>CF_DIB</c> (see
    ///   <c>ClipboardHistoryService.TryCaptureImage</c>).</item>
    /// </list>
    /// Previously <see cref="SetPng"/> alone left only <c>CF_PNG</c> on the
    /// clipboard, so BYH's history (and most Windows apps) couldn't see the
    /// screenshot — that was the "history doesn't capture Ocean Eyes screenshots"
    /// bug. Writing both formats in one open/empty/set sequence is atomic: no
    /// intermediate state where the clipboard holds only one format.
    /// <para>
    /// <paramref name="dib"/> is optional: if null (PNG→DIB conversion failed),
    /// only <c>CF_PNG</c> is written — best-effort degradation rather than
    /// failing the whole copy. Returns true if at least one format was placed.
    /// </para>
    /// </summary>
    public bool SetImageDibAndPng(byte[] png, byte[]? dib)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0 || png.Length > MaxDibBytes)
        {
            return false;
        }
        // Keep the write-side limit aligned with GetImageDib/SetImageDib.
        // A huge DIB is both unnecessary (the PNG is the primary modern
        // format) and dangerous: clipboard history readers may materialize it
        // synchronously on a notification thread. Degrade to PNG-only instead
        // of placing an unbounded HGLOBAL on the system clipboard.
        if (dib is { Length: 0 } or { Length: > MaxDibBytes })
        {
            dib = null; // treat empty/oversized as "no DIB, PNG only"
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        uint pngFormat = RegisterClipboardFormatW("PNG");
        if (pngFormat == 0)
        {
            return false;
        }

        nint pngMemory = 0;
        nint dibMemory = 0;
        bool pngTransferred = false;
        bool dibTransferred = false;
        try
        {
            pngMemory = AllocateGlobal(png);
            if (dib is not null)
            {
                dibMemory = AllocateGlobal(dib);
            }

            bool result = TryWithOpenClipboard(
                () =>
                {
                    if (!EmptyClipboard())
                    {
                        return false;
                    }
                    pngTransferred = SetClipboardData(pngFormat, pngMemory) != 0;
                    if (dibMemory != 0)
                    {
                        dibTransferred = SetClipboardData(CfDib, dibMemory) != 0;
                    }
                    // Success if at least one format landed. PNG is the primary
                    // artifact (matches what SetPng always wrote); DIB is the
                    // compatibility bonus. If PNG failed but DIB ok, still true
                    // (the image is on the clipboard either way).
                    return pngTransferred || dibTransferred;
                },
                out bool placed) && placed;
            return result;
        }
        finally
        {
            // Each HGLOBAL's ownership is decided independently from the
            // SetClipboardData return value. Never re-query here: another
            // clipboard listener can replace/free the data before the query,
            // making a second GlobalFree an AV/double-free.
            if (pngMemory != 0 && !pngTransferred)
            {
                GlobalFree(pngMemory);
            }
            if (dibMemory != 0 && !dibTransferred)
            {
                GlobalFree(dibMemory);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the clipboard as CF_UNICODETEXT,
    /// replacing the current contents. Mirrors <see cref="SetPng"/>: opens with
    /// bounded retry, empties, allocates a movable HGLOBAL with a NUL-terminated
    /// UTF-16 copy, and hands ownership to the system on success. Returns false
    /// on empty input, size cap, or open/set failure. Used by R54 to paste a
    /// history entry back onto the clipboard.
    /// </summary>
    public bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return false;
        }

        // Enforce the same byte budget as GetText (MaxTextBytes covers UTF-16).
        int byteCount = checked((text.Length + 1) * 2);
        if (byteCount > MaxTextBytes)
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
        nint memory = AllocateGlobal(bytes);
        bool transferred = false;
        try
        {
            bool result = TryWithOpenClipboard(
                () =>
                {
                    if (!EmptyClipboard())
                    {
                        return false;
                    }
                    transferred = SetClipboardData(CfUnicodeText, memory) != 0;
                    return transferred;
                },
                out bool placed) && placed;
            return result;
        }
        finally
        {
            // SetClipboardData transfers ownership on success. Do not query
            // clipboard availability after CloseClipboard: a listener can
            // replace/free the data in between, making that query race with a
            // second GlobalFree and causing a delayed access violation.
            if (memory != 0 && !transferred)
            {
                GlobalFree(memory);
            }
        }
    }

    /// <summary>
    /// Returns the process name (lowercased, no extension, e.g. <c>chrome</c>)
    /// of the current foreground window, or null when it cannot be determined.
    /// Used by R54 to implement the exclude-apps privacy filter — the foreground
    /// window at clipboard-change time is the source of the copied content.
    /// </summary>
    public string? GetForegroundProcessName()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName?.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Process already exited.
            return null;
        }
        catch (Win32Exception)
        {
            // Access denied (e.g. elevated process).
            return null;
        }
    }

    public void SubscribeChanges(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_subscriptionGate)
        {
            if (_changeCallback is not null)
            {
                throw new InvalidOperationException("A clipboard change subscription is already active.");
            }

            _changeCallback = onChanged;
            if (!AddClipboardFormatListener(_windowHandle))
            {
                _changeCallback = null;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AddClipboardFormatListener failed.");
            }
        }
    }

    public void UnsubscribeChanges()
    {
        lock (_subscriptionGate)
        {
            if (_changeCallback is null)
            {
                return;
            }

            _changeCallback = null;
            RemoveClipboardFormatListener(_windowHandle);
        }
    }

    private bool TryWithOpenClipboard<T>(Func<T> operation, out T? result)
    {
        lock (_clipboardGate)
        {
            var stopwatch = Stopwatch.StartNew();
            int delayMilliseconds = 2;

            do
            {
                if (OpenClipboard(_windowHandle))
                {
                    try
                    {
                        result = operation();
                        return true;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }

                Thread.Sleep(delayMilliseconds);
                delayMilliseconds = Math.Min(delayMilliseconds * 2, 20);
            }
            while (stopwatch.Elapsed < _openTimeout);

            result = default;
            return false;
        }
    }

    private static string? TryReadUnicodeText(int maxBytes)
    {
        nint memory = GetClipboardData(CfUnicodeText);
        if (memory == 0)
        {
            return null;
        }

        nuint byteCount = GlobalSize(memory);
        if (byteCount == 0 || byteCount > (nuint)maxBytes)
        {
            return null;
        }

        nint pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            int maximumCharacters = checked((int)byteCount / sizeof(char));
            char* characters = (char*)pointer;
            int length = 0;
            while (length < maximumCharacters && characters[length] != '\0')
            {
                length++;
            }

            return new string(characters, 0, length);
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    private static byte[]? TryReadGlobalBytes(uint format, int maxBytes)
    {
        nint memory = GetClipboardData(format);
        if (memory == 0)
        {
            return null;
        }

        nuint byteCount = GlobalSize(memory);
        if (byteCount == 0 || byteCount > (nuint)maxBytes)
        {
            return null;
        }

        nint pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            byte[] bytes = new byte[checked((int)byteCount)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    /// <summary>P2 memory: ArrayPool-backed variant of
    /// <see cref="TryReadGlobalBytes"/>. Returns the rented buffer (which may be
    /// larger than <paramref name="maxBytes"/> or the actual payload — ArrayPool
    /// rounds up to a bucket) plus the actual byte count read. The caller owns
    /// the buffer and must <c>ArrayPool.Return</c> it. Used by <see cref="Backup"/>
    /// so the CF_DIB (up to 32 MB) is not allocated on the NativeAOT large-object
    /// heap on every selection probe. Returns (null, 0) on any failure.</summary>
    private static (byte[]? buffer, int length) TryReadGlobalBytesPooled(
        uint format, int maxBytes)
    {
        nint memory = GetClipboardData(format);
        if (memory == 0)
        {
            return (null, 0);
        }

        nuint byteCount = GlobalSize(memory);
        if (byteCount == 0 || byteCount > (nuint)maxBytes)
        {
            return (null, 0);
        }

        nint pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            return (null, 0);
        }

        try
        {
            int len = checked((int)byteCount);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                Marshal.Copy(pointer, buffer, 0, len);
                return (buffer, len);
            }
            catch
            {
                // Privacy: clear on the failure path too — a partial Marshal.Copy
                // may have written clipboard bytes before throwing.
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                throw;
            }
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    private static string[]? TryReadFiles()
    {
        nint dropHandle = GetClipboardData(CfHDrop);
        if (dropHandle == 0)
        {
            return null;
        }

        uint count = DragQueryFile(dropHandle, uint.MaxValue, null, 0);
        if (count == 0 || count > MaxFileCount)
        {
            return null;
        }

        var files = new List<string>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            uint length = DragQueryFile(dropHandle, index, null, 0);
            if (length == 0 || length >= MaxPathChars)
            {
                return null;
            }

            var path = new StringBuilder(checked((int)length + 1));
            if (DragQueryFile(dropHandle, index, path, checked((uint)path.Capacity)) == 0)
            {
                return null;
            }

            files.Add(path.ToString());
        }

        return files.ToArray();
    }

    private static List<OwnedClipboardMemory> CreateSnapshotMemory(ClipboardSnapshot snapshot)
    {
        var allocations = new List<OwnedClipboardMemory>(3);
        try
        {
            if (snapshot.Text is not null)
            {
                allocations.Add(new OwnedClipboardMemory(
                    CfUnicodeText,
                    AllocateGlobal(Encoding.Unicode.GetBytes(snapshot.Text + '\0'))));
            }

            if (snapshot.ImageDib is not null && snapshot.ImageDibLength > 0)
            {
                // P2: copy only the valid prefix (ImageDibLength). The buffer may
                // be an oversized ArrayPool rental; copying the whole array would
                // write garbage padding into the restored CF_DIB and waste HGLOBAL.
                allocations.Add(new OwnedClipboardMemory(CfDib,
                    AllocateGlobal(snapshot.ImageDib, snapshot.ImageDibLength)));
            }

            if (snapshot.Files is not null)
            {
                allocations.Add(new OwnedClipboardMemory(CfHDrop, AllocateGlobal(CreateDropFiles(snapshot.Files))));
            }

            return allocations;
        }
        catch
        {
            foreach (OwnedClipboardMemory allocation in allocations)
            {
                allocation.Dispose();
            }

            throw;
        }
    }

    private static nint AllocateGlobal(byte[] bytes)
    {
        nint memory = GlobalAlloc(GmemMoveable | GmemZeroInit, checked((nuint)bytes.Length));
        if (memory == 0)
        {
            throw new OutOfMemoryException("GlobalAlloc failed for clipboard data.");
        }

        nint pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            GlobalFree(memory);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed for clipboard data.");
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(memory);
        }

        return memory;
    }

    /// <summary>P2: allocates a GlobalAlloc HGLOBAL of exactly
    /// <paramref name="length"/> bytes and copies the first
    /// <paramref name="length"/> bytes of <paramref name="bytes"/> into it. Used
    /// by Restore when <paramref name="bytes"/> is an oversized ArrayPool rental
    /// (the valid payload is <paramref name="length"/>, not bytes.Length).</summary>
    private static nint AllocateGlobal(byte[] bytes, int length)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if ((uint)length > (uint)bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        nint memory = GlobalAlloc(GmemMoveable | GmemZeroInit, checked((nuint)length));
        if (memory == 0)
        {
            throw new OutOfMemoryException("GlobalAlloc failed for clipboard data.");
        }

        nint pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            GlobalFree(memory);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed for clipboard data.");
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, length);
        }
        finally
        {
            GlobalUnlock(memory);
        }

        return memory;
    }

    private static byte[] CreateDropFiles(string[] files)
    {
        const int dropFilesHeaderSize = 20;
        string paths = string.Join('\0', files) + "\0\0";
        byte[] encodedPaths = Encoding.Unicode.GetBytes(paths);
        byte[] data = new byte[dropFilesHeaderSize + encodedPaths.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0, sizeof(uint)), (uint)dropFilesHeaderSize);
        BitConverter.TryWriteBytes(data.AsSpan(16, sizeof(int)), 1);
        encodedPaths.CopyTo(data, dropFilesHeaderSize);
        return data;
    }

    private void MessageThreadMain()
    {
        try
        {
            nint module = GetModuleHandle(null);
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = SharedWindowProcedure,
                Instance = module,
                ClassName = _windowClassName,
            };

            _windowClassAtom = RegisterClassEx(ref windowClass);
            if (_windowClassAtom == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW failed.");
            }

            _windowHandle = CreateWindowEx(
                0,
                _windowClassName,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                0,
                module,
                0);
            if (_windowHandle == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed.");
            }

            Instances[_windowHandle] = new WeakReference<Win32Clipboard>(this);
            _started.Set();

            while (GetMessage(out NativeMessage message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _started.Set();
        }
        finally
        {
            if (_windowHandle != 0)
            {
                Instances.TryRemove(_windowHandle, out _);
                _windowHandle = 0;
            }

            if (_windowClassAtom != 0)
            {
                UnregisterClass(_windowClassName, GetModuleHandle(null));
                _windowClassAtom = 0;
            }
        }
    }

    private void RaiseClipboardChanged()
    {
        Action? callback;
        lock (_subscriptionGate)
        {
            callback = _changeCallback;
        }

        try
        {
            callback?.Invoke();
        }
        catch
        {
            // Native message processing must remain alive even if a subscriber fails.
        }
    }

    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == WmClipboardUpdate &&
            Instances.TryGetValue(window, out WeakReference<Win32Clipboard>? reference) &&
            reference.TryGetTarget(out Win32Clipboard? instance))
        {
            instance.RaiseClipboardChanged();
            return 0;
        }

        if (message == WmClose)
        {
            DestroyWindow(window);
            return 0;
        }

        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        UnsubscribeChanges();
        nint window = _windowHandle;
        if (window != 0)
        {
            PostMessage(window, WmClose, 0, 0);
        }

        if (_messageThread != Thread.CurrentThread)
        {
            _messageThread.Join(TimeSpan.FromSeconds(2));
        }

        _started.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class OwnedClipboardMemory : IDisposable
    {
        private bool _transferred;

        public OwnedClipboardMemory(uint format, nint handle)
        {
            Format = format;
            Handle = handle;
        }

        public uint Format { get; }

        public nint Handle { get; }

        public void TransferOwnership() => _transferred = true;

        public void Dispose()
        {
            if (!_transferred && Handle != 0)
            {
                GlobalFree(Handle);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WindowProcedure? WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string? ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint newOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(nint window);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);

    // B1 (StringBuilder): migrated in a later batch — LibraryImport StringBuilder support is limited.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint drop, uint fileIndex, StringBuilder? fileName, uint characterCount);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandle(string? moduleName);

    // B3 (struct with embedded string + delegate): migrated in a later batch.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static partial nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClass(string className, nint instance);
}

/// <summary>P2 memory: the pooled result of <see cref="Win32Clipboard.GetImageDibPooled"/>.
/// <see cref="Buffer"/> is an ArrayPool rental that may be larger than
/// <see cref="Length"/> (bucket rounding); read only <see cref="Length"/> bytes.
/// Dispose (via <c>using</c>) returns the buffer to the pool, clearing it first
/// (privacy). The default value is an empty payload (no buffer, no-op Dispose).
/// </summary>
public sealed class ImageDibPayload : IDisposable
{
    private Action? _disposeHook;

    /// <summary>Singleton empty payload: no buffer, IsEmpty true, Dispose no-op.
    /// Returned by <see cref="Win32Clipboard.GetImageDibPooled"/> on failure /
    /// no-image so callers never get null.</summary>
    public static ImageDibPayload Empty { get; } = new();

    private ImageDibPayload()
    {
        Buffer = null!;
        Length = 0;
        _disposeHook = null;
    }

    /// <summary>Internal ctor used by <see cref="Win32Clipboard.GetImageDibPooled"/>.</summary>
    internal ImageDibPayload(byte[] buffer, int length, Action disposeHook)
    {
        Buffer = buffer;
        Length = length;
        _disposeHook = disposeHook;
    }

    /// <summary>The rented CF_DIB buffer. Read only <see cref="Length"/> bytes.</summary>
    public byte[] Buffer { get; }

    /// <summary>The number of valid DIB bytes in <see cref="Buffer"/>.</summary>
    public int Length { get; }

    /// <summary>True when the clipboard had no image / the read failed.</summary>
    public bool IsEmpty => Buffer is null || Length == 0;

    /// <summary>Convenience: a <see cref="ReadOnlySpan{T}"/> over the valid bytes.</summary>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <summary>Returns the buffer to the pool (clears it). Idempotent.</summary>
    public void Dispose() => Interlocked.Exchange(ref _disposeHook, null)?.Invoke();
}
