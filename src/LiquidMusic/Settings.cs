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
    public static readonly string[] Genres = ["Хип-хоп","Рок","Электроника","Поп","Инди","Метал","Альтернатива","Джаз","Релакс","Лоу-фай","R&B","Классика","Техно","Ретро"];
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
        Value["fx"] ??= JsonSerializer.SerializeToNode(new { glass=true,clear=false });
        Value["service"] ??= "yandex";
        Value["quality"] ??= "high";
        Value["on_top"] ??= true;
    }
    public string Service => Value["service"]?.GetValue<string>() == "youtube" ? "youtube" : "yandex";
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
