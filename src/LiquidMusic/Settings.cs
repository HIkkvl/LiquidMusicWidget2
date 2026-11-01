using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiquidMusic;

internal static class AppPaths
{
    public static string Root { get; } = FindRoot();
    public static string Logs => Path.Combine(Root, "logs");
    public static string Data => Path.Combine(Root, ".native-data");
    public static string Model => Path.Combine(AppContext.BaseDirectory, "models", "vosk-model-small-ru-0.22");
    private static string FindRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "settings.json")) && Directory.Exists(Path.Combine(path.FullName, "ui"))) return path.FullName;
        return AppContext.BaseDirectory;
    }
    public static void Log(object? error)
    {
        try
        {
            Directory.CreateDirectory(Logs);
            var path = Path.Combine(Logs, "native.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}\n");
        }
        catch { }
    }
}

internal sealed class Settings
{
    public static readonly Dictionary<string, string> Actions = new()
    {
        ["toggle"]="Показать / скрыть", ["next"]="Следующий трек", ["previous"]="Предыдущий трек",
        ["play_pause"]="Пауза / продолжить", ["stop"]="Остановить воспроизведение", ["like"]="Лайк / убрать лайк", ["voice"]="Голосовая команда"
    };
    private readonly string path;
    public JsonObject Value { get; private set; }
    public Settings(string? file = null)
    {
        path = file ?? Path.Combine(AppPaths.Root, "settings.json");
        try { Value = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new(); }
        catch { Value = new(); }
        Value["bindings"] ??= Defaults();
        foreach (var pair in Defaults()) if (Value["bindings"]![pair.Key] == null) Value["bindings"]![pair.Key] = pair.Value?.DeepClone();
        Value["overlay"] ??= JsonSerializer.SerializeToNode(new { enabled=true,corner="top-right",duration=4 });
        Value["wheel"] ??= JsonSerializer.SerializeToNode(new { enabled=true,modifier="ctrl",step=5 });
        Value["drop"] ??= JsonSerializer.SerializeToNode(new { enabled=true,side="top" });
        Value["liquid_mood"] ??= JsonSerializer.SerializeToNode(new { enabled=false });
        if (Value["liquid_mood"] is JsonObject mood) mood["enabled"] ??= false;
        else Value["liquid_mood"] = JsonSerializer.SerializeToNode(new { enabled=false });
        Value["fx"] ??= JsonSerializer.SerializeToNode(new { glass=true,clear=true,anim=true });
        if (Value["fx"] is JsonObject fx)
        {
            fx["glass"] ??= true; fx["clear"] ??= true; fx["anim"] ??= true;
            // старое значение clear=false (по умолчанию в прежней версии) не должно выключать новый эффект
            if (Value["fx_v"]?.GetValue<int>() != 2) { fx["clear"] = true; Value["fx_v"] = 2; }
        }
        Value["island_theme"] = NormalizeIslandTheme(Value["island_theme"]);
        Value["discord"] = NormalizeDiscord(Value["discord"]);
        Value["design"] = NormalizeDesign(Value["design"]);
        Value["service"] ??= "yandex";
        Value["quality"] ??= "high";
        Value["on_top"] ??= true;
    }
    private static readonly string[] IslandPresets = ["aurora", "ocean", "sunset", "mint", "violet", "graphite", "abyss", "custom"];
    /// <summary>Цветовая схема острова: известный пресет и три цвета «Свои» строго в формате #rrggbb (иначе значения по умолчанию).</summary>
    internal static JsonObject NormalizeIslandTheme(JsonNode? value)
    {
        static string Hex(JsonNode? node, string fallback) =>
            node is JsonValue v && v.TryGetValue<string>(out var text) && System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$") ? text.ToLowerInvariant() : fallback;
        var source = value as JsonObject;
        var preset = source?["preset"] is JsonValue p && p.TryGetValue<string>(out var name) && IslandPresets.Contains(name) ? name : "aurora";
        var custom = source?["custom"] as JsonObject;
        return new JsonObject
        {
            ["preset"] = preset,
            ["custom"] = new JsonObject { ["dark"] = Hex(custom?["dark"], "#2b0a1d"), ["accent"] = Hex(custom?["accent"], "#ff5ea8"), ["light"] = Hex(custom?["light"], "#ffe4f1") }
        };
    }
    /// <summary>Оформление виджета: «aurora» (по умолчанию) или «abyss».</summary>
    internal static string NormalizeDesign(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var name) && (name is "aurora" or "abyss") ? name : "aurora";
    /// <summary>Статус в Discord: включён по умолчанию, но работает только с Application ID из Discord Developer Portal.</summary>
    internal static JsonObject NormalizeDiscord(JsonNode? value)
    {
        var source = value as JsonObject;
        bool Flag(string key, bool fallback) => source?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        var id = source?["client_id"] is JsonValue c && c.TryGetValue<string>(out var text) ? text.Trim() : "";
        return new JsonObject
        {
            ["enabled"] = Flag("enabled", true),
            ["client_id"] = DiscordPresence.ValidClientId(id) ? id : "",
            ["show_artwork"] = Flag("show_artwork", true),
            ["show_paused"] = Flag("show_paused", false)
        };
    }
    public string Service => Value["service"]?.GetValue<string>() is string service&&MusicServices.Supported(service)?service:"yandex";
    public static JsonObject Defaults()
    {
        var result = new JsonObject();
        foreach (var (action,key) in new[] {("toggle","Num0"),("next","Num6"),("previous","Num4"),("play_pause","Num5"),("stop","Num2"),("like","Num1"),("voice","Num3")})
            result[action] = JsonSerializer.SerializeToNode(new {key,ctrl=false,alt=false,shift=false});
        return result;
    }
    public void Save()
    {
        var temp = path + ".native.tmp";
        File.WriteAllText(temp, Value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
