using System.Globalization;
using System.Text.RegularExpressions;

namespace SelectionAssistant.Core.Capture;

/// <summary>
/// R49: one row in the screenshot gallery. <see cref="Timestamp"/> is parsed
/// from the file name when possible (<c>ocean-eyes-yyyyMMdd-HHmmss.png</c>),
/// falling back to <c>File.GetLastWriteTime</c>. <see cref="DisplayName"/> is
/// a localized relative label ("今天 14:30" / "昨天 09:12" / "2026-07-15 17:00").
/// </summary>
public sealed record ScreenshotGalleryEntry(
    string FilePath,
    DateTime Timestamp,
    string DisplayName);

/// <summary>
/// R49: scans the Ocean Eyes save folder for <c>ocean-eyes-*.png</c> files and
/// returns them newest-first. Pure function, no UI deps, fully testable.
/// </summary>
public static partial class ScreenshotGalleryLoader
{
    [GeneratedRegex(@"ocean-eyes-(\d{8})-(\d{6})\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    private static readonly string[] DayOfWeekLabels =
    {
        "周日", "周一", "周二", "周三", "周四", "周五", "周六"
    };

    /// <summary>
    /// Returns all <c>ocean-eyes-*.png</c> in <paramref name="directory"/>,
    /// newest-first. Returns an empty list if the directory does not exist
    /// or contains no matching files.
    /// </summary>
    public static IReadOnlyList<ScreenshotGalleryEntry> Scan(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<ScreenshotGalleryEntry>();
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "ocean-eyes-*.png");
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<ScreenshotGalleryEntry>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<ScreenshotGalleryEntry>();
        }

        var entries = new List<ScreenshotGalleryEntry>();
        foreach (string file in files)
        {
            DateTime ts = ParseTimestampFromName(Path.GetFileName(file))
                          ?? SafeGetLastWriteTime(file);
            entries.Add(new ScreenshotGalleryEntry(file, ts, FormatDisplayName(ts)));
        }

        entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return entries;
    }

    /// <summary>
    /// Parses <c>ocean-eyes-yyyyMMdd-HHmmss.png</c> into a <see cref="DateTime"/>.
    /// Returns null if the file name does not match the pattern.
    /// </summary>
    public static DateTime? ParseTimestampFromName(string fileName)
    {
        Match match = NamePattern().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        // yyyyMMdd-HHmmss → "20260720-143022" (date=8, time=6 digits).
        if (DateTime.TryParseExact(
                match.Groups[1].Value + match.Groups[2].Value,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out DateTime parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>
    /// Formats a timestamp as a localized relative label.
    /// <list type="bullet">
    ///   <item>Same day: "今天 14:30"</item>
    ///   <item>Yesterday: "昨天 09:12"</item>
    ///   <item>Within 7 days: "周三 17:00"</item>
    ///   <item>Older: "2026-07-15 17:00"</item>
    /// </list>
    /// </summary>
    public static string FormatDisplayName(DateTime ts)
    {
        DateTime now = DateTime.Now;
        DateTime tsDate = ts.Date;
        DateTime today = now.Date;
        string time = ts.ToString("HH:mm", CultureInfo.InvariantCulture);

        int dayDiff = (today - tsDate).Days;
        if (dayDiff == 0)
        {
            return "今天 " + time;
        }
        if (dayDiff == 1)
        {
            return "昨天 " + time;
        }
        if (dayDiff > 0 && dayDiff < 7)
        {
            return DayOfWeekLabels[(int)ts.DayOfWeek] + " " + time;
        }
        return ts.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static DateTime SafeGetLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.Now;
        }
    }
}
