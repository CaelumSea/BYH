using System.Diagnostics;
using System.Text.RegularExpressions;
using SelectionAssistant.Infrastructure.Configuration;

namespace SelectionAssistant.Infrastructure.Logging;

/// <summary>
/// Minimal diagnostic logger that never accepts selected text and redacts common
/// credential-shaped values before writing to disk.
/// </summary>
public sealed partial class RedactedLogger
{
    private readonly object _writeGate = new();
    private readonly string _logPath;

    public RedactedLogger(string? logPath = null)
    {
        _logPath = logPath ?? ByhApplicationPaths.CreateDefault().LogFile;
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
            }
        }
        catch
        {
            // Diagnostics must never take down the selection path.
        }
    }

    private static string NormalizeSingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    [GeneratedRegex("(?i)(api[-_ ]?key|authorization|bearer|token|secret)\\s*[:=]?\\s*([^\\s,;]+)")]
    private static partial Regex SecretPattern();
}
