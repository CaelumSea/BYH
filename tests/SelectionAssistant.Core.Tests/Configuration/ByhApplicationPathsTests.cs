using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class ByhApplicationPathsTests
{
    [Fact]
    public void Paths_AreRootedUnderProvidedBaseDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "BYH-path-tests", Guid.NewGuid().ToString("N"));
        var paths = new ByhApplicationPaths(root);

        Assert.Equal(Path.Combine(root, "capture-policies.json"), paths.CapturePolicyFile);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(root, "logs", "BYH.log"), paths.LogFile);
    }

    [Fact]
    public void EnsureDirectories_CreatesBaseAndLogsDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "BYH-path-tests", Guid.NewGuid().ToString("N"));
        var paths = new ByhApplicationPaths(root);

        try
        {
            paths.EnsureDirectories();

            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(paths.LogsDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
