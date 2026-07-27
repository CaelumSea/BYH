using SelectionAssistant.Infrastructure.Logging;
using Xunit;

namespace SelectionAssistant.Core.Tests.Logging;

public sealed class RedactedLoggerTests
{
    private static string NewScratchDir() =>
        Path.Combine(Path.GetTempPath(), "BYH-logger-tests", Guid.NewGuid().ToString("N"));

    private static string LogPathIn(string dir) => Path.Combine(dir, "BYH.log");

    [Fact]
    public void Info_WritesTimestampedLine_RedactsSecrets()
    {
        string root = NewScratchDir();
        try
        {
            var logger = new RedactedLogger(LogPathIn(root));

            logger.Info("TestCategory", "loaded api_key=abc123 and bearer xyz for processing");

            string content = File.ReadAllText(LogPathIn(root));
            Assert.Contains("[INFO]", content);
            Assert.Contains("[TestCategory]", content);
            Assert.Contains("[REDACTED]", content);
            Assert.DoesNotContain("abc123", content);
            Assert.DoesNotContain("bearer xyz", content);
            // Single line (NormalizeSingleLine collapses CR/LF).
            Assert.Single(content.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Error_WithException_IncludesTypeNameAndMessage()
    {
        string root = NewScratchDir();
        try
        {
            var logger = new RedactedLogger(LogPathIn(root));

            logger.Error("TestCategory", "boom", new NullReferenceException("nothing here"));

            string content = File.ReadAllText(LogPathIn(root));
            Assert.Contains("[ERROR]", content);
            Assert.Contains("NullReferenceException", content);
            Assert.Contains("nothing here", content);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_RotatesWhenExceedingMaximumFileBytes()
    {
        string root = NewScratchDir();
        try
        {
            var logger = new RedactedLogger(LogPathIn(root));

            // Each write is ~2 KB; ~700 writes pushes the file well past 1 MB.
            string chunk = new string('x', 2000);
            for (int i = 0; i < 700; i++)
            {
                logger.Info("Burst", chunk);
            }

            string activeLog = LogPathIn(root);
            Assert.True(new FileInfo(activeLog).Length <= RedactedLogger.MaximumFileBytes,
                $"active log must shrink below cap after rotation, was {new FileInfo(activeLog).Length}");

            string[] archives = Directory.GetFiles(root, "BYH-*.log");
            Assert.NotEmpty(archives);
            Assert.All(archives, p => Assert.StartsWith("BYH-", Path.GetFileName(p)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rotation_KeepsOnlyRetainedCount()
    {
        string root = NewScratchDir();
        try
        {
            Directory.CreateDirectory(root);
            // Seed 7 pre-existing archives, all older than the rotation we'll trigger.
            // Names are deliberately timestamp-shaped so the sort used for trimming
            // treats them like real archives.
            for (int i = 0; i < 7; i++)
            {
                string stamp = $"2026010{i}-120000";
                File.WriteAllText(Path.Combine(root, $"BYH-{stamp}.log"), "old");
            }

            var logger = new RedactedLogger(LogPathIn(root));
            // Force a rotation past the cap.
            string chunk = new string('x', 2000);
            for (int i = 0; i < 700; i++)
            {
                logger.Info("Burst", chunk);
            }

            string[] archives = Directory.GetFiles(root, "BYH-*.log");
            Assert.InRange(archives.Length, 1, RedactedLogger.RetainedRotations);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_ArchivesOversizedExistingFile()
    {
        string root = NewScratchDir();
        try
        {
            Directory.CreateDirectory(root);
            string logPath = LogPathIn(root);
            // Pre-write a file over the cap so construction must rotate it away.
            File.WriteAllText(logPath, new string('y', (int)RedactedLogger.MaximumFileBytes + 4096));

            var logger = new RedactedLogger(logPath);

            string[] archives = Directory.GetFiles(root, "BYH-*.log");
            Assert.Single(archives);
            // The oversized content moved out of the active log entirely.
            // After startup rotation the active log is simply absent until the
            // first write lands — that is the contract: rotate the leftover
            // away, let the next Write create a fresh file.
            Assert.False(File.Exists(logPath));
            // The archived content was preserved verbatim.
            string archived = File.ReadAllText(archives[0]);
            Assert.True(archived.Length > RedactedLogger.MaximumFileBytes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_LeavesSmallExistingFile_Alone()
    {
        string root = NewScratchDir();
        try
        {
            Directory.CreateDirectory(root);
            string logPath = LogPathIn(root);
            const string seed = "tiny pre-existing content";
            File.WriteAllText(logPath, seed);

            var logger = new RedactedLogger(logPath);
            logger.Info("After", "construction");

            string content = File.ReadAllText(logPath);
            Assert.StartsWith(seed, content);
            Assert.Contains("[After]", content);
            Assert.Empty(Directory.GetFiles(root, "BYH-*.log"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_CreatesDirectoryAndWrites_WhenMissing()
    {
        string root = NewScratchDir();
        try
        {
            // Point at a path whose directory does not exist yet.
            string nestedDir = Path.Combine(root, "deep", "logs");
            string logPath = Path.Combine(nestedDir, "BYH.log");

            var logger = new RedactedLogger(logPath);
            logger.Info("BootTest", "first write into a fresh tree");

            Assert.True(Directory.Exists(nestedDir));
            string content = File.ReadAllText(logPath);
            Assert.Contains("[BootTest]", content);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rotation_ArchiveNameDisambiguatesSameSecond()
    {
        string root = NewScratchDir();
        try
        {
            var logger = new RedactedLogger(LogPathIn(root));

            // Force two rotations within the same second: fill past the cap,
            // then fill the freshly-rotated file past the cap again.
            string chunk = new string('x', 2000);
            for (int i = 0; i < 700; i++) logger.Info("Burst", chunk);
            for (int i = 0; i < 700; i++) logger.Info("Burst", chunk);

            string[] archives = Directory.GetFiles(root, "BYH-*.log");
            Assert.True(archives.Length >= 2, $"expected >=2 archives, got {archives.Length}");
            // Either two distinct timestamps, or one timestamp plus a `-2` suffixed
            // sibling — both are valid disambiguations.
            var names = archives.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
