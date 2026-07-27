using SelectionAssistant.Core.Appearance;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class UserProfileStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-profile-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_UsesWindowsUserName()
    {
        UserProfileSettings settings = UserProfileStore.LoadIfExists(TempPath());

        Assert.False(string.IsNullOrWhiteSpace(settings.DisplayName));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsUnicodeDisplayName()
    {
        string path = TempPath();
        try
        {
            UserProfileStore.Save(
                new UserProfileSettings { DisplayName = "小王" },
                path);

            UserProfileSettings loaded = UserProfileStore.LoadIfExists(path);

            Assert.Equal("小王", loaded.DisplayName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BlankDisplayName_RestoresSystemDefault()
    {
        UserProfileSettings normalized = new UserProfileSettings
        {
            DisplayName = "   ",
        }.Normalize();

        Assert.Equal(UserProfileSettings.Default.DisplayName, normalized.DisplayName);
    }

    [Fact]
    public void InvalidDisplayName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new UserProfileSettings { DisplayName = "line\nbreak" }.Validate());
        Assert.Throws<ArgumentException>(() =>
            new UserProfileSettings { DisplayName = new string('x', 33) }.Validate());
    }
}
