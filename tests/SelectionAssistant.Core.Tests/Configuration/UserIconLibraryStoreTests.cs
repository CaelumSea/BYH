using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class UserIconLibraryStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-user-icons-{Guid.NewGuid():N}.json");

    private const string SvgWrapOpen =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" " +
        "fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">";
    private const string SvgWrapClose = "</svg>";

    // ── ExtractPathData ──

    [Fact]
    public void ExtractPathData_Path_ReturnsDAttribute()
    {
        string svg = SvgWrapOpen + "<path d=\"M2 2 L10 10\" />" + SvgWrapClose;
        string? data = UserIconLibraryStore.ExtractPathData(svg);
        Assert.Equal("M2 2 L10 10", data);
    }

    [Fact]
    public void ExtractPathData_Circle_SynthesizesTwoArcs()
    {
        string svg = SvgWrapOpen + "<circle cx=\"12\" cy=\"12\" r=\"5\" />" + SvgWrapClose;
        string? data = UserIconLibraryStore.ExtractPathData(svg);
        Assert.NotNull(data);
        // Two arc commands around (12,12) with r=5 → starts at (7,12).
        Assert.Contains("M 7,12", data);
        Assert.Contains("a 5,5 0 1,0 10,0", data);
        Assert.Contains("a 5,5 0 1,0 -10,0", data);
    }

    [Fact]
    public void ExtractPathData_Line_BecomesMoveLine()
    {
        string svg = SvgWrapOpen + "<line x1=\"3\" y1=\"3\" x2=\"21\" y2=\"21\" />" + SvgWrapClose;
        string? data = UserIconLibraryStore.ExtractPathData(svg);
        Assert.Equal("M 3,3 L 21,21", data);
    }

    [Fact]
    public void ExtractPathData_Rect_BecomesClosedBox()
    {
        string svg = SvgWrapOpen + "<rect x=\"3\" y=\"3\" width=\"18\" height=\"18\" />" + SvgWrapClose;
        string? data = UserIconLibraryStore.ExtractPathData(svg);
        Assert.NotNull(data);
        Assert.Contains("M 3,3", data);
        Assert.Contains("L 21,3", data);
        Assert.Contains("L 21,21", data);
        Assert.Contains("L 3,21", data);
        Assert.EndsWith(" Z", data);
    }

    [Fact]
    public void ExtractPathData_Polygon_BecomesClosedPolyline()
    {
        string svg = SvgWrapOpen + "<polygon points=\"12,2 22,20 2,20\" />" + SvgWrapClose;
        string? data = UserIconLibraryStore.ExtractPathData(svg);
        Assert.NotNull(data);
        Assert.StartsWith("M 12,2", data);
        Assert.Contains("L 22,20", data);
        Assert.Contains("L 2,20", data);
        Assert.EndsWith(" Z", data);
    }

    [Fact]
    public void ExtractPathData_MultipleShapes_CombinesIntoOneString()
    {
        string svg = SvgWrapOpen +
                     "<path d=\"M1 1\" />" +
                     "<circle cx=\"5\" cy=\"5\" r=\"2\" />" +
                     SvgWrapClose;
        string? data = UserIconLibraryStore.ExtractPathData(svg);
        Assert.NotNull(data);
        Assert.Contains("M1 1", data);
        Assert.Contains("M 3,5", data); // circle start (cx-r)
    }

    [Fact]
    public void ExtractPathData_NoGeometry_ReturnsNull()
    {
        string svg = SvgWrapOpen + "<g></g>" + SvgWrapClose;
        Assert.Null(UserIconLibraryStore.ExtractPathData(svg));
    }

    [Fact]
    public void ExtractPathData_DeGeneratedCircle_ZeroRadius_Skipped()
    {
        // r=0 produces nothing usable; must not throw or emit a degenerate arc.
        string svg = SvgWrapOpen + "<circle cx=\"5\" cy=\"5\" r=\"0\" />" + SvgWrapClose;
        Assert.Null(UserIconLibraryStore.ExtractPathData(svg));
    }

    // ── NameFromFile ──

    [Theory]
    [InlineData("tag.svg", "tag")]
    [InlineData("my-cool-icon.svg", "my cool icon")]
    [InlineData("under_scored.SVG", "under scored")]
    [InlineData("/path/to/Folder Star.svg", "Folder Star")]
    public void NameFromFile_StripsExtensionAndSeparators(string file, string expected)
    {
        Assert.Equal(expected, UserIconLibraryStore.NameFromFile(file));
    }

    // ── AddIcons ──

    [Fact]
    public void AddIcons_NewIcons_AppendsAll()
    {
        var lib = UserIconLibrary.Empty;
        var result = UserIconLibraryStore.AddIcons(lib,
        [
            new UserIcon("tag", "M1"),
            new UserIcon("star", "M2"),
        ]);
        Assert.Equal(2, result.Icons.Count);
    }

    [Fact]
    public void AddIcons_SameNameSameData_NoDuplicate()
    {
        var lib = UserIconLibraryStore.AddIcons(UserIconLibrary.Empty,
        [
            new UserIcon("tag", "M1"),
        ]);
        var result = UserIconLibraryStore.AddIcons(lib,
        [
            new UserIcon("tag", "M1"),
        ]);
        Assert.Single(result.Icons);
    }

    [Fact]
    public void AddIcons_SameNameDifferentData_Uniquifies()
    {
        var lib = UserIconLibraryStore.AddIcons(UserIconLibrary.Empty,
        [
            new UserIcon("tag", "M1"),
        ]);
        var result = UserIconLibraryStore.AddIcons(lib,
        [
            new UserIcon("tag", "M2"),
        ]);
        Assert.Equal(2, result.Icons.Count);
        Assert.Contains(result.Icons, i => i.Name == "tag" && i.PathData == "M1");
        Assert.Contains(result.Icons, i => i.Name == "tag (2)" && i.PathData == "M2");
    }

    [Fact]
    public void AddIcons_BlankNameOrData_Skipped()
    {
        var result = UserIconLibraryStore.AddIcons(UserIconLibrary.Empty,
        [
            new UserIcon("", "M1"),
            new UserIcon("ok", "  "),
        ]);
        Assert.Empty(result.Icons);
    }

    // ── RemoveIcon ──

    [Fact]
    public void RemoveIcon_Existing_Removes()
    {
        var lib = UserIconLibraryStore.AddIcons(UserIconLibrary.Empty,
        [
            new UserIcon("a", "M1"),
            new UserIcon("b", "M2"),
        ]);
        var result = UserIconLibraryStore.RemoveIcon(lib, "a");
        Assert.Single(result.Icons);
        Assert.Equal("b", result.Icons[0].Name);
    }

    [Fact]
    public void RemoveIcon_Absent_NoOp()
    {
        var lib = UserIconLibraryStore.AddIcons(UserIconLibrary.Empty,
        [
            new UserIcon("a", "M1"),
        ]);
        var result = UserIconLibraryStore.RemoveIcon(lib, "zzz");
        Assert.Same(lib, result);
    }

    // ── Load / Save round-trip ──

    [Fact]
    public void SaveLoad_RoundTripsIcons()
    {
        string path = TempPath();
        try
        {
            var lib = UserIconLibraryStore.AddIcons(UserIconLibrary.Empty,
            [
                new UserIcon("tag", "M1"),
                new UserIcon("star", "M2 L3"),
            ]);
            UserIconLibraryStore.Save(path, lib);
            var loaded = UserIconLibraryStore.Load(path);
            Assert.Equal(2, loaded.Icons.Count);
            Assert.Contains(loaded.Icons, i => i.Name == "tag" && i.PathData == "M1");
            Assert.Contains(loaded.Icons, i => i.Name == "star" && i.PathData == "M2 L3");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var loaded = UserIconLibraryStore.Load(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json"));
        Assert.Same(UserIconLibrary.Empty, loaded);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json }}}");
            var loaded = UserIconLibraryStore.Load(path);
            Assert.Same(UserIconLibrary.Empty, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
