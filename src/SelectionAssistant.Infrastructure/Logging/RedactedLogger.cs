using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SelectionAssistant.Infrastructure.Configuration;

namespace SelectionAssistant.Infrastructure.Logging;

/// <summary>
/// Minimal diagnostic logger that never accepts selected text and redacts common
/// credential-shaped values before writing to disk. Writes are size-capped via
/// rolling rotation: once <see cref="MaximumFileBytes"/> is exceeded the current
/// file is archived as <c>BYH-yyyyMMdd-HHmmss.log</c> (with <c>-2</c>, <c>-3</c>,
/// … disambiguation inside the same second) and only the most recent
/// <see cref="RetainedRotations"/> archives are kept; older ones are deleted.
/// </summary>
public sealed partial class RedactedLogger
{
    /// <summary>Soft cap at which the active <c>BYH.log</c> rolls over. Matches
    /// the <c>MaximumFileBytes</c> convention used across the config stores.</summary>
    public const long MaximumFileBytes = 1 * 1024 * 1024; // 1 MB

    /// <summary>Number of archived rotations kept in the logs directory. Older
    /// archives are deleted when a new rotation brings the total above this.</summary>
    public const int RetainedRotations = 5;

    private static readonly string ArchiveFilePrefix = "BYH-";
    private static readonly string ArchiveFileSuffix = ".log";

    private readonly object _writeGate = new();
    private readonly string _logPath;
    private readonly string _logDirectory;

    public RedactedLogger(string? logPath = null)
    {
        _logPath = logPath ?? ByhApplicationPaths.CreateDefault().LogFile;
        _logDirectory = Path.GetDirectoryName(_logPath) ?? string.Empty;

        // Roll any oversized leftover log (e.g. an older build that predates
        // rotation) out of the way before we start appending. Failure here must
        // never break startup — diagnostics are best-effort.
        try { TryRotateOnStartup(); }
        catch { /* best-effort: swallow to protect the selection path */ }
    }

    public string LogPath => _logPath;

    public void Info(string category, string message) => Write("INFO", category, message);

    public void Error(string category, string message, Exception? exception = null)
    {
        string detail = exception is null
            ? message
            : $"{message} ({exception.GetType().Name}: {exception.Message})";
        Write("ERROR", category, detail);
    }

    private void Write(string level, string category, string message)
    {
        string safeCategory = NormalizeSingleLine(category);
        string safeMessage = SecretPattern().Replace(NormalizeSingleLine(message), "$1[REDACTED]");
        string line = $"{DateTimeOffset.Now:O} [{level}] [{safeCategory}] {safeMessage}";

        Trace.WriteLine(line);
        try
        {
            lock (_writeGate)
            {
                string? directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_logPath, line + Environment.NewLine);

                // After appending, check whether the file has crossed the cap and
                // roll it over so the next write starts on a fresh file. Done
                // inside the same lock so concurrent writers on this instance
                // can't race the rename.
                FileInfo info = new(_logPath);
                if (info.Exists && info.Length > MaximumFileBytes)
                {
                    TryRotateNow();
                }
            }
        }
        catch
        {
            // Diagnostics must never take down the selection path.
        }
    }

    private void TryRotateOnStartup()
    {
        if (!File.Exists(_logPath))
        {
            return;
        }

        try
        {
            if (new FileInfo(_logPath).Length > MaximumFileBytes)
            {
                lock (_writeGate)
                {
                    // Re-check inside the lock — another writer may have rolled
                    // it already between the size probe and acquiring the gate.
                    if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaximumFileBytes)
                    {
                        TryRotateNow();
                    }
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Renames the active log to a timestamped archive (resolving
    /// same-second collisions with <c>-2</c>, <c>-3</c> … suffixes) and trims
    /// the directory down to <see cref="RetainedRotations"/> archives. Assumes
    /// the caller holds <see cref="_writeGate"/>.</summary>
    private void TryRotateNow()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                // Another instance already moved it (we share the file with
                // App.axaml.cs's clipboardLogger). Nothing to do.
                return;
            }

            if (string.IsNullOrEmpty(_logDirectory))
            {
                return;
            }

            string archivePath = ResolveArchivePath();
            File.Move(_logPath, archivePath, overwrite: false);
            TrimOldArchives();
        }
        catch
        {
            // If the rename fails (locked, disk full, …) we simply leave the
            // current file in place — the next write will retry the rotation.
        }
    }

    private string ResolveArchivePath()
    {
        // Filenames sort chronologically because the timestamp is fixed-width,
        // so the same ordering doubles as "newest first" for trimming.
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string baseName = $"{ArchiveFilePrefix}{stamp}{ArchiveFileSuffix}";
        string candidate = Path.Combine(_logDirectory, baseName);

        int suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(_logDirectory, $"{ArchiveFilePrefix}{stamp}-{suffix}{ArchiveFileSuffix}");
            suffix++;
        }

        return candidate;
    }

    private void TrimOldArchives()
    {
        string[] archives;
        try
        {
            archives = Directory.GetFiles(_logDirectory, $"{ArchiveFilePrefix}*{ArchiveFileSuffix}");
        }
        catch
        {
            return;
        }

        // Sort newest-first by filename (timestamp prefix sorts the same way as
        // creation time, and avoids touching the FS again per file).
        Array.Sort(archives, StringComparer.Ordinal);
        Array.Reverse(archives);

        for (int i = RetainedRotations; i < archives.Length; i++)
        {
            try { File.Delete(archives[i]); }
            catch { /* leave a stray archive rather than failing the rotation */ }
        }
    }

    private static string NormalizeSingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    [GeneratedRegex("(?i)(api[-_ ]?key|authorization|bearer|token|secret)\\s*[:=]?\\s*([^\\s,;]+)")]
    private static partial Regex SecretPattern();
}
