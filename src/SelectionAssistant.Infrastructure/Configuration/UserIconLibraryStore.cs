using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// R54 v1.2 v6: user-imported icon library. Users import SVG files (a single
/// icon or a whole icon-pack folder); each SVG's path/circle/line geometry is
/// extracted into an Avalonia <c>Geometry.Parse</c>-compatible path-data string
/// and stored here. Rendering then uses the native Avalonia <c>Path</c> +
/// <c>StreamGeometry</c> — <b>no SVG NuGet package required</b>, and it is
/// NativeAOT/trimmer-safe (regex + string parsing, no reflection).
/// </summary>
/// <remarks>
/// <b>Storage value convention</b>: a tag icon stored as <c>user:&lt;name&gt;</c>
/// resolves to an entry here; <c>lucide:&lt;slug&gt;</c> resolves against the
/// built-in <c>LucideIcons</c> catalog; a bare emoji char renders as text.
/// </remarks>
public sealed record UserIconLibrary(IReadOnlyList<UserIcon> Icons)
{
    public static readonly string StoragePrefix = "user:";
    public static UserIconLibrary Empty { get; } = new([]);
}

/// <summary>A single user-imported icon: a display name + path-data geometry.
/// The name is the stable storage key (used in <c>user:&lt;name&gt;</c>).</summary>
public sealed record UserIcon(string Name, string PathData);

/// <summary>Pure helpers for loading, saving, and mutating the user icon
/// library, plus SVG→path-data extraction. Mirrors the ClipboardTagStore
/// pattern: every mutator returns a NEW record; the caller persists.</summary>
public static class UserIconLibraryStore
{
    // ── SVG geometry extraction ──
    //
    // Lucide/Tabler/Feather/Phosphor-style icon packs are single-color line
    // icons on a 24×24 (or similar) viewBox, drawn with <path>, <circle>, and
    // <line> elements. We extract each shape's geometry into an SVG-path-data
    // string that Avalonia's Geometry.Parse accepts verbatim (M/L/A/C/Z for
    // paths; circles are synthesized as two arc moves; lines as M/L). Polygons
    // and rects are also handled. Fill/stroke attributes are dropped — the
    // Avalonia Path controls its own Stroke.
    private static readonly Regex PathTagRe =
        new(@"<path\b([^/>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CircleTagRe =
        new(@"<circle\b([^/>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineTagRe =
        new(@"<line\b([^/>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex RectTagRe =
        new(@"<rect\b([^/>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex PolygonTagRe =
        new(@"<polygon\b([^/>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex AttrRe =
        new(@"(\w[\w-]*)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex AttrDRe =
        new(@"\bd\s*=\s*""([^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);

    private static Dictionary<string, string> ParseAttrs(string attrs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in AttrRe.Matches(attrs))
        {
            d[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return d;
    }

    private static double? Num(Dictionary<string, string> a, string key)
    {
        if (a.TryGetValue(key, out string? v) &&
            double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n))
        {
            return n;
        }
        return null;
    }

    /// <summary>Extracts a single Avalonia-parseable path-data string from an
    /// SVG document. Returns null when no drawable geometry was found.</summary>
    public static string? ExtractPathData(string svg)
    {
        ArgumentNullException.ThrowIfNull(svg);
        var datas = new List<string>();

        // <path d="...">
        foreach (Match m in PathTagRe.Matches(svg))
        {
            Match dm = AttrDRe.Match(m.Groups[1].Value);
            if (dm.Success && !string.IsNullOrWhiteSpace(dm.Groups[1].Value))
            {
                datas.Add(dm.Groups[1].Value.Trim());
            }
        }
        // <circle cx cy r> → two arcs
        foreach (Match m in CircleTagRe.Matches(svg))
        {
            var a = ParseAttrs(m.Groups[1].Value);
            double cx = Num(a, "cx") ?? 0;
            double cy = Num(a, "cy") ?? 0;
            double r = Num(a, "r") ?? 0;
            if (r <= 0) continue;
            datas.Add($"M {cx - r},{cy} a {r},{r} 0 1,0 {2 * r},0 a {r},{r} 0 1,0 {-2 * r},0");
        }
        // <line x1 y1 x2 y2> → M/L
        foreach (Match m in LineTagRe.Matches(svg))
        {
            var a = ParseAttrs(m.Groups[1].Value);
            double? x1 = Num(a, "x1"), y1 = Num(a, "y1");
            double? x2 = Num(a, "x2"), y2 = Num(a, "y2");
            if (x1.HasValue && y1.HasValue && x2.HasValue && y2.HasValue)
            {
                datas.Add($"M {x1},{y1} L {x2},{y2}");
            }
        }
        // <rect x y width height> → four lines
        foreach (Match m in RectTagRe.Matches(svg))
        {
            var a = ParseAttrs(m.Groups[1].Value);
            double x = Num(a, "x") ?? 0;
            double y = Num(a, "y") ?? 0;
            double w = Num(a, "width") ?? 0;
            double h = Num(a, "height") ?? 0;
            if (w <= 0 || h <= 0) continue;
            datas.Add($"M {x},{y} L {x + w},{y} L {x + w},{y + h} L {x},{y + h} Z");
        }
        // <polygon points="x,y x,y ..."> → polyline closed
        foreach (Match m in PolygonTagRe.Matches(svg))
        {
            var a = ParseAttrs(m.Groups[1].Value);
            if (!a.TryGetValue("points", out string? pts) || string.IsNullOrWhiteSpace(pts)) continue;
            string[] coords = pts.Trim().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (coords.Length < 2) continue;
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (string c in coords)
            {
                sb.Append(first ? "M " : " L ");
                sb.Append(c.Trim());
                first = false;
            }
            sb.Append(" Z");
            datas.Add(sb.ToString());
        }

        if (datas.Count == 0) return null;
        return string.Join(' ', datas);
    }

    /// <summary>Derives a display name from an SVG file name (strips extension,
    /// replaces - and _ with spaces). Caller may further dedupe.</summary>
    public static string NameFromFile(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        return name.Replace('-', ' ').Replace('_', ' ').Trim();
    }

    // ── Persistence ──
    //
    // Uses the AOT-safe JsonDocument + Utf8JsonWriter pattern (same as
    // ClipboardTagStore) rather than reflection-based JsonSerializer, so the
    // app's NativeAOT publish stays at 0 trim/AOT warnings. Atomic write via a
    // temp file + File.Move(overwrite), so a crash mid-write never corrupts the
    // existing library.

    public const int MaximumFileBytes = 4 * 1024 * 1024; // 4 MB cap.

    public static UserIconLibrary Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return UserIconLibrary.Empty;
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes) return UserIconLibrary.Empty;
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("icons", out JsonElement iconsEl) ||
                iconsEl.ValueKind != JsonValueKind.Array)
            {
                return UserIconLibrary.Empty;
            }
            var icons = new List<UserIcon>();
            foreach (JsonElement item in iconsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out JsonElement nameEl) ||
                    !item.TryGetProperty("pathData", out JsonElement dataEl)) continue;
                string? name = nameEl.GetString();
                string? data = dataEl.GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(data)) continue;
                icons.Add(new UserIcon(name.Trim(), data));
            }
            return icons.Count == 0 ? UserIconLibrary.Empty : new UserIconLibrary(icons);
        }
        catch
        {
            return UserIconLibrary.Empty;
        }
    }

    public static void Save(string path, UserIconLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("icons");
                foreach (UserIcon ic in library.Icons)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", ic.Name);
                    writer.WriteString("pathData", ic.PathData);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Best-effort: clean up the temp file if the write/move failed.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* swallow */ }
            throw;
        }
    }

    /// <summary>Adds icons to the library, deduping by name (later wins). Names
    /// are uniquified by appending " (2)", " (3)", … when a name already exists
    /// with different path-data. Returns the new library.</summary>
    public static UserIconLibrary AddIcons(UserIconLibrary library, IEnumerable<UserIcon> additions)
    {
        ArgumentNullException.ThrowIfNull(library);
        var byName = new Dictionary<string, UserIcon>(StringComparer.Ordinal);
        foreach (UserIcon ic in library.Icons)
        {
            byName[ic.Name] = ic;
        }
        foreach (UserIcon add in additions)
        {
            if (string.IsNullOrWhiteSpace(add.Name) || string.IsNullOrWhiteSpace(add.PathData))
            {
                continue;
            }
            string name = add.Name.Trim();
            // If the name exists with identical path-data, skip (re-import no-op).
            if (byName.TryGetValue(name, out UserIcon? existing) &&
                string.Equals(existing.PathData, add.PathData, StringComparison.Ordinal))
            {
                continue;
            }
            // Uniquify if the name exists with different data.
            string unique = name;
            int n = 2;
            while (byName.ContainsKey(unique) &&
                   !string.Equals(byName[unique].PathData, add.PathData, StringComparison.Ordinal))
            {
                unique = $"{name} ({n++})";
            }
            byName[unique] = new UserIcon(unique, add.PathData);
        }
        return new UserIconLibrary(byName.Values.OrderBy(i => i.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>Removes the icon with the given name. No-op if absent.</summary>
    public static UserIconLibrary RemoveIcon(UserIconLibrary library, string name)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (string.IsNullOrEmpty(name)) return library;
        var kept = library.Icons.Where(i => !string.Equals(i.Name, name, StringComparison.Ordinal)).ToList();
        if (kept.Count == library.Icons.Count) return library;
        return new UserIconLibrary(kept);
    }

    // ── JSON doc shape (camelCase) ──
    private sealed class Doc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<DocIcon>? Icons { get; set; }
    }

    private sealed class DocIcon
    {
        public string Name { get; set; } = string.Empty;
        public string PathData { get; set; } = string.Empty;
    }
}
