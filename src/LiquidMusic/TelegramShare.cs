using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LiquidMusic;

/// <summary>
/// Отправка карточки текущего трека (название, артист, альбом, обложка, ссылка в сервисе) в личного Telegram-бота пользователя.
/// Аудиофайлы не скачиваются и не отправляются. Токен бота хранится зашифрованным для текущего пользователя Windows и в интерфейс не возвращается.
/// </summary>
internal sealed class TelegramShare(Settings settings)
{
    private static readonly Regex TokenPattern = new(@"^\d{6,12}:[A-Za-z0-9_-]{30,50}$", RegexOptions.Compiled);
    private static readonly Regex ChatPattern = new(@"^-?\d{5,15}$", RegexOptions.Compiled);
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime last = DateTime.MinValue;
    private static string TokenFile => Path.Combine(ProfileStorage.Root, "telegram.bin");
    private string ChatId => settings.Value["telegram_chat"]?.GetValue<string>() ?? "";

    private static string? ReadToken()
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException) { return null; }
    }

    internal string? BotToken() => ReadToken();
    internal string BotChat() => ChatId;

    internal JsonObject Status()
    {
        var token = ReadToken();
        return new() { ["ok"] = true, ["configured"] = token != null && ChatId.Length > 0, ["has_token"] = token != null, ["chat_id"] = ChatId, ["token_tail"] = token == null ? "" : token[^4..] };
    }

    /// <param name="input">token (пусто — оставить прежний), chat_id, clear_token.</param>
    internal JsonObject Configure(JsonObject input)
    {
        if (input["clear_token"]?.GetValue<bool>() == true)
        {
            try { File.Delete(TokenFile); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("Не удалось удалить токен."); }
            settings.Value["telegram_chat"] = ""; settings.Save();
            return Status();
        }
        var chat = (input["chat_id"]?.GetValue<string>() ?? "").Trim();
        if (!ChatPattern.IsMatch(chat)) throw new ArgumentException("Chat ID — число (для группы со знаком минус).");
        var token = (input["token"]?.GetValue<string>() ?? "").Trim();
        if (token.Length > 0)
        {
            if (!TokenPattern.IsMatch(token)) throw new ArgumentException("Токен похож на 123456789:AA… — скопируйте его целиком из @BotFather.");
            Directory.CreateDirectory(ProfileStorage.Root);
            File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        }
        else if (ReadToken() == null) throw new ArgumentException("Вставьте токен бота.");
        settings.Value["telegram_chat"] = chat; settings.Save();
        return Status();
    }

    private static string Clip(JsonNode? node, int max)
    {
        var value = node is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim() : "";
        return value.Length > max ? value[..max] : value;
    }

    internal async Task SendTrackAsync(JsonObject snapshot)
    {
        var token = ReadToken();
        if (token == null || ChatId.Length == 0) throw new InvalidOperationException("Сначала укажите токен бота и Chat ID: Настройки → Telegram.");
        var title = Clip(snapshot["title"], 200);
        if (title.Length == 0 || title == "В ожидании музыки") throw new InvalidOperationException("Сейчас ничего не играет.");
        var artist = Clip(snapshot["artist"], 200); var album = Clip(snapshot["album"], 200);
        var service = snapshot["service"]?.GetValue<string>() is string id && MusicServices.Supported(id) ? MusicServices.Name(id) : "";
        var link = Clip(snapshot["track_url"], 1000);
        if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri) || linkUri.Scheme != Uri.UriSchemeHttps) link = "";
        var artwork = Clip(snapshot["artwork"], 1000);
        if (!Uri.TryCreate(artwork, UriKind.Absolute, out var artUri) || artUri.Scheme != Uri.UriSchemeHttps) artwork = "";

        var caption = new StringBuilder("🎵 <b>" + WebUtility.HtmlEncode(title) + "</b>");
        if (artist.Length > 0) caption.Append('\n').Append(WebUtility.HtmlEncode(artist));
        if (album.Length > 0) caption.Append("\n<i>").Append(WebUtility.HtmlEncode(album)).Append("</i>");
        if (service.Length > 0) caption.Append('\n').Append(WebUtility.HtmlEncode(service));
        var body = new JsonObject { ["chat_id"] = ChatId, ["parse_mode"] = "HTML" };
        if (link.Length > 0)
            body["reply_markup"] = new JsonObject { ["inline_keyboard"] = new JsonArray(new JsonArray(new JsonObject { ["text"] = "Открыть" + (service.Length > 0 ? " в " + service : ""), ["url"] = link })) };

        await gate.WaitAsync();
        try
        {
            if (DateTime.UtcNow - last < TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Подождите пару секунд перед следующей отправкой.");
            var sent = false;
            if (artwork.Length > 0)
            {
                var photo = (JsonObject)body.DeepClone(); photo["photo"] = artwork; photo["caption"] = caption.ToString();
                sent = (await CallAsync(token, "sendPhoto", photo, throwOnFailure: false)) == null;   // обложка недоступна для Telegram — отправим текстом
            }
            if (!sent)
            {
                var text = (JsonObject)body.DeepClone(); text["text"] = caption.ToString(); text["disable_web_page_preview"] = true;
                await CallAsync(token, "sendMessage", text, throwOnFailure: true);
            }
            last = DateTime.UtcNow;
        }
        finally { gate.Release(); }
    }

    /// <returns>null — успех; иначе описание ошибки (если throwOnFailure=false).</returns>
    private static async Task<string?> CallAsync(string token, string method, JsonObject body, bool throwOnFailure)
    {
        string description;
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await TelegramNet.Client.PostAsync("https://api.telegram.org/bot" + token + "/" + method, content);
            var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
            if (answer?["ok"]?.GetValue<bool>() == true) return null;
            description = answer?["description"]?.GetValue<string>() ?? "HTTP " + (int)response.StatusCode;
        }
        // Сообщения этих исключений содержат адрес запроса с токеном: наружу и в журнал их не отдаём.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { description = "Не удалось связаться с Telegram. Проверьте интернет или укажите прокси в настройках."; }
        if (!throwOnFailure) return description;
        if (description.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)) description = "Telegram не принял токен бота. Проверьте токен.";
        else if (description.Contains("chat not found", StringComparison.OrdinalIgnoreCase) || description.Contains("bot can't initiate", StringComparison.OrdinalIgnoreCase) || description.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            description = "Бот не может написать в этот чат. Откройте бота в Telegram и нажмите Start, затем проверьте Chat ID.";
        throw new InvalidOperationException(description.Length > 200 ? description[..200] : description);
    }
}
