using System.Globalization;
using SelectionAssistant.Core.Capture;
using Xunit;

namespace SelectionAssistant.Core.Tests.Capture;

[Trait("Category", "Capture")]
public sealed class ScreenshotGalleryLoaderTests
{
    private static readonly byte[] DummyPngHeader =
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"oe-gallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteDummyPng(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, DummyPngHeader);
        return path;
    }

    [Fact]
    public void Scan_NonExistentDirectory_ReturnsEmpty()
    {
        string ghost = Path.Combine(Path.GetTempPath(), $"ghost-{Guid.NewGuid():N}");
        var entries = ScreenshotGalleryLoader.Scan(ghost);
        Assert.Empty(entries);
    }

    [Fact]
    public void Scan_EmptyDirectory_ReturnsEmpty()
    {
        string dir = TempDir();
        try
        {
            var entries = ScreenshotGalleryLoader.Scan(dir);
            Assert.Empty(entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Scan_ReturnsOceanEyesPngFilesOnly()
    {
        string dir = TempDir();
        try
        {
            WriteDummyPng(dir, "ocean-eyes-20260720-143022.png");
            WriteDummyPng(dir, "ocean-eyes-20260720-150000.png");
            WriteDummyPng(dir, "random-note.txt");
            WriteDummyPng(dir, "vacation.jpg");
            WriteDummyPng(dir, "screenshot.png"); // wrong prefix

            var entries = ScreenshotGalleryLoader.Scan(dir);
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.EndsWith(".png", e.FilePath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Scan_ReturnsNewestFirst()
    {
        string dir = TempDir();
        try
        {
            WriteDummyPng(dir, "ocean-eyes-20260718-100000.png");
            WriteDummyPng(dir, "ocean-eyes-20260720-143022.png");
            WriteDummyPng(dir, "ocean-eyes-20260719-080000.png");

            var entries = ScreenshotGalleryLoader.Scan(dir);
            Assert.Equal(3, entries.Count);
            Assert.True(entries[0].Timestamp > entries[1].Timestamp);
            Assert.True(entries[1].Timestamp > entries[2].Timestamp);
            Assert.Contains("20260720-143022", entries[0].FilePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseTimestampFromName_ValidFile_ParsesCorrectly()
    {
        DateTime? ts = ScreenshotGalleryLoader.ParseTimestampFromName(
            "ocean-eyes-20260720-143022.png");
        Assert.NotNull(ts);
        Assert.Equal(2026, ts!.Value.Year);
        Assert.Equal(7, ts.Value.Month);
        Assert.Equal(20, ts.Value.Day);
        Assert.Equal(14, ts.Value.Hour);
        Assert.Equal(30, ts.Value.Minute);
        Assert.Equal(22, ts.Value.Second);
    }

    [Fact]
    public void ParseTimestampFromName_InvalidName_ReturnsNull()
    {
        Assert.Null(ScreenshotGalleryLoader.ParseTimestampFromName("ocean-eyes-xyz.png"));
        Assert.Null(ScreenshotGalleryLoader.ParseTimestampFromName("vacation.png"));
        Assert.Null(ScreenshotGalleryLoader.ParseTimestampFromName("ocean-eyes-20260720-1430.png"));
    }

    [Fact]
    public void Scan_FallsBackToFileWriteTime_WhenNameInvalid()
    {
        string dir = TempDir();
        try
        {
            string path = WriteDummyPng(dir, "ocean-eyes-manual-edit.png");
            var info = new FileInfo(path);
            var marker = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Local);
            info.LastWriteTime = marker;

            var entries = ScreenshotGalleryLoader.Scan(dir);
            Assert.Single(entries);
            Assert.Equal(marker, entries[0].Timestamp);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FormatDisplayName_Today_ReturnsTodayLabel()
    {
        DateTime now = DateTime.Now;
        string label = ScreenshotGalleryLoader.FormatDisplayName(
            new DateTime(now.Year, now.Month, now.Day, 14, 30, 0, DateTimeKind.Local));
        Assert.StartsWith("今天 ", label);
        Assert.EndsWith("14:30", label);
    }

    [Fact]
    public void FormatDisplayName_Yesterday_ReturnsYesterdayLabel()
    {
        DateTime yesterday = DateTime.Now.Date.AddDays(-1);
        DateTime ts = new DateTime(yesterday.Year, yesterday.Month, yesterday.Day, 9, 12, 0, DateTimeKind.Local);
        string label = ScreenshotGalleryLoader.FormatDisplayName(ts);
        Assert.StartsWith("昨天 ", label);
        Assert.EndsWith("09:12", label);
    }

    [Fact]
    public void FormatDisplayName_OlderThanWeek_ReturnsIsoLabel()
    {
        // 14 days ago — well beyond the 7-day window.
        DateTime old = DateTime.Now.Date.AddDays(-14);
        DateTime ts = new DateTime(old.Year, old.Month, old.Day, 17, 0, 0, DateTimeKind.Local);
        string label = ScreenshotGalleryLoader.FormatDisplayName(ts);

        string expected = ts.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(expected, label);
    }
}
