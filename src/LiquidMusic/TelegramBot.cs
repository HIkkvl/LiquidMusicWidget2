using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiquidMusic;

/// <summary>Общий HTTP-клиент для Telegram (бот и отправка трека) с необязательным прокси: http://, https://, socks5://, socks4://.</summary>
internal static class TelegramNet
{
    private static HttpClient client = Create(null);
    public static HttpClient Client => Volatile.Read(ref client);

    /// <returns>Нормализованный адрес прокси (пусто — без прокси). Бросает ArgumentException для неверного значения.</returns>
    public static string Normalize(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return "";
        if (!value.Contains("://")) value = "socks5://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.IsDefaultPort && uri.Scheme.StartsWith("socks"))
            throw new ArgumentException("Прокси: укажите адрес вида socks5://127.0.0.1:1080 или http://127.0.0.1:8080.");
        if (uri.Scheme is not ("http" or "https" or "socks5" or "socks4" or "socks4a"))
            throw new ArgumentException("Прокси: поддерживаются http, https, socks5 и socks4. MTProto-прокси для ботов не подходит.");
        return value;
    }

    public static void SetProxy(string? value) => Volatile.Write(ref client, Create(string.IsNullOrWhiteSpace(value) ? null : value));

    private static HttpClient Create(string? proxy)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (proxy != null && Uri.TryCreate(proxy, UriKind.Absolute, out var uri))
        {
            var web = new WebProxy(new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri);
            if (uri.UserInfo.Length > 0)
            {
                var parts = uri.UserInfo.Split(':', 2);
                web.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            handler.Proxy = web; handler.UseProxy = true;
        }
        else handler.UseProxy = false;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };   // long polling 25 с + запас
    }
}

/// <summary>
/// Управление плеером из личного Telegram-бота (long polling, без открытых портов и без webhook).
/// Бот отвечает ТОЛЬКО в чате, указанном в настройках (Chat ID); сообщения и кнопки из других чатов молча игнорируются.
/// Токен берётся из TelegramShare и нигде не логируется: тексты сетевых исключений с адресом запроса наружу не отдаются.
/// </summary>
internal sealed class TelegramBot(
    Func<string?> token,
    Func<string> chatId,
    Func<Task<JsonObject>> getState,
    Func<string, string?, double?, Task<JsonObject>> execute) : IAsyncDisposable
{
    private sealed class ApiException(string message, string kind = "error") : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    private readonly object sync = new();
    private CancellationTokenSource? cts;
    private Task? loop;
    private string status = "stopped", detail = "";

    private const string HelpText =
        "🎧 <b>Liquid Music</b>\nУправляю плеером на вашем ПК.\n\n" +
        "/now — что играет сейчас\n" +
        "/play — продолжить · <code>/play название</code> — найти и включить\n" +
        "/pause · /stop · /next · /prev · /like\n" +
        "/vol 40 — громкость (также <code>+10</code> и <code>-10</code>)\n\n" +
        "Любой текст без «/» — тоже поиск и запуск трека.";

    private static readonly JsonObject Commands = new()
    {
        ["commands"] = new JsonArray(
            Cmd("now", "Что играет сейчас"), Cmd("play", "Продолжить или найти трек"), Cmd("pause", "Пауза"),
            Cmd("next", "Следующий трек"), Cmd("prev", "Предыдущий трек"), Cmd("like", "Лайк"),
            Cmd("vol", "Громкость: /vol 40, +10, -10"), Cmd("help", "Помощь"))
    };
    private static JsonObject Cmd(string command, string description) => new() { ["command"] = command, ["description"] = description };

    // ---------- управление ----------
    public bool Running { get { lock (sync) return loop != null; } }

    public JsonObject Status() { lock (sync) return new() { ["running"] = loop != null, ["status"] = status, ["detail"] = detail }; }

    public void Start()
    {
        lock (sync)
        {
            if (loop != null) return;
            cts = new();
            var ct = cts.Token;
            status = "starting"; detail = "";
            loop = Task.Run(() => RunAsync(ct));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? source; Task? running;
        lock (sync) { source = cts; running = loop; cts = null; loop = null; status = "stopped"; detail = ""; }
        if (source == null) return;
        source.Cancel();
        try { if (running != null) await running.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        source.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Set(string state, string text = "") { lock (sync) { if (loop != null) { status = state; detail = text; } } }

    private static async Task<bool> PauseAsync(int milliseconds, CancellationToken ct)
    {
        try { await Task.Delay(milliseconds, ct); return true; } catch (OperationCanceledException) { return false; }
    }

    private static string Describe(Exception error) => error is ApiException or InvalidOperationException ? error.Message : error.GetType().Name;

    // ---------- основной цикл ----------
    private async Task RunAsync(CancellationToken ct)
    {
        long offset = 0;
        var failures = 0;
        var primed = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var key = token(); var chat = chatId();
                if (key == null || chat.Length == 0)
                {
                    Set("no_config", "Укажите токен бота и Chat ID.");
                    primed = false;
                    if (!await PauseAsync(3000, ct)) break;
                    continue;
                }
                if (!primed)
                {
                    // Команды, накопившиеся, пока бот был выключен, не выполняем задним числом.
                    var last = await CallAsync(key, "getUpdates", new() { ["offset"] = -1, ["timeout"] = 0 }, ct);
                    if (last is JsonArray { Count: > 0 } items && items[items.Count - 1]?["update_id"] is JsonValue lastId && lastId.TryGetValue<long>(out var lastNumber))
                        offset = lastNumber + 1;
                    await TryCallAsync(key, "setMyCommands", Commands, ct);
                    primed = true;
                }
                var updates = await CallAsync(key, "getUpdates", new() { ["offset"] = offset, ["timeout"] = 25, ["allowed_updates"] = new JsonArray("message", "callback_query") }, ct);
                Set("connected");
                failures = 0;
                if (updates is not JsonArray list) continue;
                foreach (var update in list.OfType<JsonObject>())
                {
                    if (update["update_id"] is JsonValue id && id.TryGetValue<long>(out var number)) offset = Math.Max(offset, number + 1);
                    try { await HandleAsync(key, chat, update, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) { AppPaths.Log("Telegram bot: " + Describe(error)); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                failures++;
                var message = Describe(error);
                Set(error is ApiException api ? api.Kind : "error", message);
                AppPaths.Log("Telegram bot: " + message);
                if (!await PauseAsync(Math.Min(30000, 1500 * (1 << Math.Min(failures, 5))), ct)) break;
            }
        }
    }

    // ---------- обработка сообщений ----------
    private static string Id(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : "";
    private static string Short(string text, int max = 200) => text.Length > max ? text[..max] + "…" : text;
    private static string Enc(string text) => WebUtility.HtmlEncode(text);
    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim() : "";
    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
    private static string Clock(double seconds) { var total = (int)Math.Max(0, seconds); return $"{total / 60}:{total % 60:00}"; }

    private async Task HandleAsync(string key, string chat, JsonObject update, CancellationToken ct)
    {
        if (update["callback_query"] is JsonObject callback)
        {
            var origin = callback["message"] as JsonObject;
            if (Id(origin?["chat"]?["id"]) != chat) return;   // нажатия из чужих чатов игнорируем
            string? note;
            try { note = await RunButtonAsync(callback["data"]?.GetValue<string>() ?? "", ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { note = "⚠️ " + Short(error.Message, 150); }
            await TryCallAsync(key, "answerCallbackQuery", new() { ["callback_query_id"] = callback["id"]?.GetValue<string>() ?? "", ["text"] = note ?? "" }, ct);
            if (origin?["message_id"] is JsonValue messageId && messageId.TryGetValue<long>(out var messageNumber))
            {
                var (text, markup) = Panel(await getState(), null);
                await TryCallAsync(key, "editMessageText", new()
                {
                    ["chat_id"] = chat, ["message_id"] = messageNumber, ["text"] = text, ["parse_mode"] = "HTML",
                    ["disable_web_page_preview"] = true, ["reply_markup"] = markup
                }, ct);
            }
            return;
        }

        if (update["message"] is not JsonObject message || Id(message["chat"]?["id"]) != chat) return;
        var line = Text(message["text"]);
        if (line.Length == 0) return;

        string command, argument;
        if (line[0] == '/')
        {
            var space = line.IndexOf(' ');
            var head = space < 0 ? line : line[..space];
            argument = space < 0 ? "" : line[(space + 1)..].Trim();
            var at = head.IndexOf('@');
            if (at > 0) head = head[..at];
            command = head[1..].ToLowerInvariant();
        }
        else { command = "play"; argument = line; }   // обычный текст — поиск трека

        try
        {
            string? note = null;
            switch (command)
            {
                case "start": case "help":
                    await SendAsync(key, chat, HelpText, null, ct);
                    break;
                case "now": case "status": case "panel":
                    break;
                case "pause": await execute("pause", null, null); break;
                case "stop": await execute("stop", null, null); break;
                case "play": case "resume":
                    if (argument.Length == 0) await execute("play", null, null);
                    else
                    {
                        await SendAsync(key, chat, "🔎 Ищу: " + Enc(Short(argument, 120)), null, ct);
                        await execute("search_play", argument, null);
                    }
                    break;
                case "next": await execute("next", null, null); await PauseAsync(600, ct); break;
                case "prev": case "previous": await execute("previous", null, null); await PauseAsync(600, ct); break;
                case "like": await execute("like", null, null); note = "Лайк переключён"; break;
                case "vol": case "volume": note = await SetVolumeAsync(argument); break;
                default:
                    await SendAsync(key, chat, "Не знаю такую команду. Напишите /help", null, ct);
                    return;
            }
            var (text, markup) = Panel(await getState(), note);
            await SendAsync(key, chat, text, markup, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { await SendAsync(key, chat, "⚠️ " + Enc(Short(error.Message, 300)), null, ct); }
    }

    private async Task<string?> RunButtonAsync(string data, CancellationToken ct)
    {
        switch (data)
        {
            case "pp": await execute("play_pause", null, null); await PauseAsync(400, ct); return null;
            case "next": await execute("next", null, null); await PauseAsync(600, ct); return null;
            case "prev": await execute("previous", null, null); await PauseAsync(600, ct); return null;
            case "like": await execute("like", null, null); return "Лайк переключён";
            case "vd": return await ChangeVolumeAsync(-0.1);
            case "vu": return await ChangeVolumeAsync(0.1);
            default: return null;   // «ref» — просто обновить панель
        }
    }

    private async Task<string?> ChangeVolumeAsync(double delta)
    {
        var state = await getState();
        var current = Number(state["volume"]);
        if (state["connected"] is not JsonValue c || !c.TryGetValue<bool>(out var connected) || !connected || current == null)
            throw new InvalidOperationException("Регулятор громкости сейчас недоступен.");
        var next = Math.Clamp(Math.Round((current.Value + delta) * 100) / 100, 0, 1);
        await execute("volume", null, next);
        return "Громкость " + (int)Math.Round(next * 100) + "%";
    }

    private async Task<string?> SetVolumeAsync(string argument)
    {
        var text = argument.Trim().TrimEnd('%').Trim();
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) || Math.Abs(number) > 100)
            throw new ArgumentException("Напишите, например: /vol 40, /vol +10 или /vol -10");
        if (text[0] is '+' or '-') return await ChangeVolumeAsync(number / 100.0);
        if (number < 0) throw new ArgumentException("Громкость — число от 0 до 100.");
        await execute("volume", null, number / 100.0);
        return "Громкость " + number + "%";
    }

    // ---------- карточка плеера ----------
    private static JsonObject Button(string label, string data) => new() { ["text"] = label, ["callback_data"] = data };

    private static (string Text, JsonObject Markup) Panel(JsonObject state, string? note)
    {
        var connected = state["connected"] is JsonValue c && c.TryGetValue<bool>(out var isConnected) && isConnected;
        var title = Text(state["title"]); var artist = Text(state["artist"]);
        var has = connected && title.Length > 0 && title is not ("В ожидании музыки" or "Подключение…");
        var playing = state["playing"] is JsonValue p && p.TryGetValue<bool>(out var isPlaying) && isPlaying;
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(note)) builder.Append(Enc(note)).Append("\n\n");
        if (has)
        {
            builder.Append(playing ? "▶️ " : "⏸ ").Append("<b>").Append(Enc(Short(title))).Append("</b>");
            if (artist.Length > 0) builder.Append('\n').Append(Enc(Short(artist)));
            var info = new List<string>();
            if (state["service"] is JsonValue s && s.TryGetValue<string>(out var service) && MusicServices.Supported(service)) info.Add(MusicServices.Name(service));
            if (Number(state["duration"]) is > 0 and var duration) info.Add(Clock(Math.Min(Number(state["position"]) ?? 0, duration)) + " / " + Clock(duration));
            if (Number(state["volume"]) is { } volume) info.Add("громкость " + (int)Math.Round(volume * 100) + "%");
            if (info.Count > 0) builder.Append("\n<i>").Append(Enc(string.Join(" · ", info))).Append("</i>");
        }
        else
        {
            builder.Append("Сейчас ничего не играет");
            var error = Text(state["error"]);
            if (error.Length > 0) builder.Append("\n<i>").Append(Enc(Short(error))).Append("</i>");
        }
        var markup = new JsonObject
        {
            ["inline_keyboard"] = new JsonArray(
                new JsonArray(Button("⏮", "prev"), Button(playing ? "⏸" : "▶️", "pp"), Button("⏭", "next")),
                new JsonArray(Button("🔉", "vd"), Button("❤️", "like"), Button("🔊", "vu")),
                new JsonArray(Button("🔄 Обновить", "ref")))
        };
        return (builder.ToString(), markup);
    }

    // ---------- Telegram Bot API ----------
    private static Task SendAsync(string key, string chat, string html, JsonObject? markup, CancellationToken ct)
    {
        var body = new JsonObject { ["chat_id"] = chat, ["text"] = html, ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true };
        if (markup != null) body["reply_markup"] = markup;
        return CallAsync(key, "sendMessage", body, ct);
    }

    /// <summary>Некритичный вызов: ошибки API (например, «message is not modified») не прерывают обработку.</summary>
    private static async Task TryCallAsync(string key, string method, JsonObject body, CancellationToken ct)
    {
        try { await CallAsync(key, method, body, ct); }
        catch (Exception error) when (error is ApiException or InvalidOperationException) { }
    }

    private static async Task<JsonNode?> CallAsync(string key, string method, JsonObject body, CancellationToken ct)
    {
        string description; int code = 0;
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await TelegramNet.Client.PostAsync("https://api.telegram.org/bot" + key + "/" + method, content, ct);
            var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (answer?["ok"]?.GetValue<bool>() == true) return answer["result"];
            description = answer?["description"]?.GetValue<string>() ?? "HTTP " + (int)response.StatusCode;
            code = (int)response.StatusCode;
        }
        // Сообщения этих исключений содержат адрес запроса с токеном: наружу и в журнал их не отдаём.
        catch (HttpRequestException) { throw new InvalidOperationException("Не удалось связаться с Telegram. Проверьте интернет или укажите прокси в настройках."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("Telegram не ответил вовремя."); }
        catch (JsonException) { throw new InvalidOperationException("Telegram вернул непонятный ответ."); }
        if (code == 401 || description.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            throw new ApiException("Telegram не принял токен бота. Проверьте токен.", "auth");
        if (code == 409 || description.Contains("Conflict", StringComparison.OrdinalIgnoreCase))
            throw new ApiException("Этот токен уже использует другая программа или webhook. Остановите её либо выпустите боту новый токен у @BotFather.", "conflict");
        throw new ApiException(Short(description));
    }
}
