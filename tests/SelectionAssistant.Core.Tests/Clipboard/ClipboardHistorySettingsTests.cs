using SelectionAssistant.Core.Clipboard;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardHistorySettingsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default;

        Assert.True(settings.Enabled);
        Assert.False(settings.AutoPasteEnabled);
        Assert.Equal(1000, settings.MaxEntries);
        Assert.True(settings.MaskSensitiveEnabled);
        Assert.Contains("1password", settings.ExcludeProcessNames);
    }

    [Fact]
    public void Normalize_ClampsMaxEntries()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default with { MaxEntries = 5 };
        Assert.Equal(10, settings.Normalize().MaxEntries);

        settings = ClipboardHistorySettings.Default with { MaxEntries = 99999 };
        Assert.Equal(5000, settings.Normalize().MaxEntries);
    }

    [Fact]
    public void Normalize_DeduplicatesAndTrimsExcludeList()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default with
        {
            ExcludeProcessNames = new List<string> { "  Chrome  ", "chrome", "Keepass", "  " },
        };

        ClipboardHistorySettings normalized = settings.Normalize();

        Assert.Equal(2, normalized.ExcludeProcessNames.Count);
        Assert.Contains("Chrome", normalized.ExcludeProcessNames);
        Assert.Contains("Keepass", normalized.ExcludeProcessNames);
    }

    [Fact]
    public void Validate_OutOfRangeMax_Throws()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default with { MaxEntries = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }

    // ── R54 window size ──

    [Fact]
    public void Default_HasLegacyXamlWindowSize()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default;

        Assert.Equal(800, settings.WindowWidth);
        Assert.Equal(620, settings.WindowHeight);
        settings.Validate();
    }

    [Fact]
    public void Normalize_ClampsWindowWidthToFloor()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default with
        {
            WindowWidth = ClipboardHistorySettings.MinWindowWidth - 100,
        };

        Assert.Equal(ClipboardHistorySettings.MinWindowWidth, settings.Normalize().WindowWidth);
    }

    [Fact]
    public void Normalize_ClampsWindowHeightToCeiling()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default with
        {
            WindowHeight = ClipboardHistorySettings.MaxWindowHeight + 1000,
        };

        Assert.Equal(ClipboardHistorySettings.MaxWindowHeight, settings.Normalize().WindowHeight);
    }

    [Fact]
    public void Validate_HeightOutOfRange_Throws()
    {
        ClipboardHistorySettings settings = ClipboardHistorySettings.Default with
        {
            WindowHeight = 10,
        };

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }
}
