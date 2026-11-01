namespace LiquidMusic;

/// <summary>
/// Local-only image catalogue for Liquid Mood. Categories are an allowlist so
/// a web message can never turn this into an arbitrary file or URL reader.
/// </summary>
internal static class LiquidMood
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp", ".avif"
    };

    public static string Root => Path.Combine(AppContext.BaseDirectory, "assets", "liquid-mood");

    public static string NormalizeCategory(string? category) => category switch
    {
        "cats" or "dogs" or "memes" => category,
        _ => throw new ArgumentException("Unknown Liquid Mood category")
    };

    public static void EnsureFolders(string? root = null)
    {
        foreach (var category in new[] { "cats", "dogs", "memes" })
            Directory.CreateDirectory(Path.Combine(root ?? Root, category));
    }

    public static object ListImages(string? category, string? root = null)
    {
        var selected = NormalizeCategory(category);
        var folder = Path.Combine(root ?? Root, selected);
        Directory.CreateDirectory(folder);

        var images = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .Select(path => new
            {
                name = Path.GetFileName(path),
                url = $"https://liquid-mood.local/{selected}/{Uri.EscapeDataString(Path.GetFileName(path))}"
            })
            .ToArray();

        return new
        {
            ok = true,
            category = selected,
            images,
            empty = images.Length == 0,
            folder = $"assets/liquid-mood/{selected}/"
        };
    }
}
