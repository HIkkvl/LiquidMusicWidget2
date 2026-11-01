using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiquidMusic;

internal static class LiquidMoodTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "LiquidMoodTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            LiquidMood.EnsureFolders(root);
            foreach (var category in new[] { "cats", "dogs", "memes" })
            {
                var empty = JsonSerializer.SerializeToNode(LiquidMood.ListImages(category, root))!.AsObject();
                if (empty["empty"]?.GetValue<bool>() != true || empty["images"]!.AsArray().Count != 0)
                    throw new InvalidOperationException("An empty Liquid Mood folder was not handled correctly");
            }

            File.WriteAllBytes(Path.Combine(root, "cats", "quiet cat.png"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(root, "cats", "notes.txt"), "Not an image");
            File.WriteAllText(Path.Combine(root, "dogs", "ignore.svg"), "Not an allowed image format");

            var cats = JsonSerializer.SerializeToNode(LiquidMood.ListImages("cats", root))!.AsObject();
            var image = cats["images"]!.AsArray().Single()!.AsObject();
            if (cats["images"]!.AsArray().Count != 1 || image["name"]?.GetValue<string>() != "quiet cat.png")
                throw new InvalidOperationException("Liquid Mood image filtering failed");
            if (image["url"]?.GetValue<string>() != "https://liquid-mood.local/cats/quiet%20cat.png")
                throw new InvalidOperationException("Liquid Mood local URL escaping failed");
            if (JsonSerializer.SerializeToNode(LiquidMood.ListImages("dogs", root))!["images"]!.AsArray().Count != 0)
                throw new InvalidOperationException("An unsupported image format was returned");

            try
            {
                LiquidMood.ListImages("../", root);
                throw new InvalidOperationException("Liquid Mood accepted path traversal");
            }
            catch (ArgumentException) { }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
