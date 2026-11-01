using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace LiquidMusic;

/// <summary>
/// Статус «Слушает …» в Discord через локальный IPC (именованный канал discord-ipc-N): приложение говорит только с клиентом
/// Discord на этом же ПК, свои сетевые запросы не делает. Название, исполнитель, обложка (ссылка) и время получает клиент Discord.
/// Нужен Application ID, который создаётся в Discord Developer Portal (его название и будет показано как «Слушает …»).
/// </summary>
internal sealed class DiscordPresence : IAsyncDisposable
{
    internal sealed record Options(bool Enabled, string ClientId, bool Artwork, bool ShowPaused);
    internal sealed record Track(bool Playable, string Title, string Artist, string Album, string Artwork, bool Playing, double Position, double Duration, string Service);

    private readonly object sync = new();
    private Options options = new(false, "", true, false);
    private Track track = new(false, "", "", "", "", false, 0, 0, "");
    private string status = "disabled", detail = "", user = "";
    private bool active;
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;

    public DiscordPresence() { worker = Task.Run(() => RunAsync(stop.Token)); }

    /// <summary>
    /// ID приложения Discord по умолчанию. Создайте приложение один раз на discord.com/developers и впишите его Application ID сюда:
    /// тогда ничего вводить в настройках не нужно, статус заработает сам. Пока строка пустая, ID берётся из настроек.
    /// </summary>
    internal const string DefaultClientId = "";
    internal static bool HasDefaultClientId => ValidClientId(DefaultClientId);

    // ---------- разбор данных (статические: проверяются в --self-test) ----------
    internal static bool ValidClientId(string? id) => id != null && id.Length is >= 17 and <= 20 && id.All(char.IsAsciiDigit);

    internal static Options ParseOptions(JsonNode? value)
    {
        var source = value as JsonObject;
        bool Flag(string key, bool fallback) => source?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        var id = source?["client_id"] is JsonValue c && c.TryGetValue<string>(out var text) ? text.Trim() : "";
        return new Options(Flag("enabled", true), ValidClientId(id) ? id : HasDefaultClientId ? DefaultClientId : "", Flag("show_artwork", true), Flag("show_paused", false));
    }

    internal static Track ParseTrack(JsonObject snapshot)
    {
        string Text(string key) => snapshot[key] is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : "";
        double Number(string key) => snapshot[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;
        bool Flag(string key) => snapshot[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var title = Text("title");
        var playable = Flag("connected") && title.Length > 0 && title != "В ожидании музыки" && title != "Подключение…";
        return new Track(playable, title, Text("artist"), Text("album"), Text("artwork"), Flag("playing"), Math.Max(0, Number("position")), Math.Max(0, Number("duration")), Text("service"));
    }

    /// <summary>Discord принимает строки длиной от 2 до 128 символов.</summary>
    internal static string Clip(string text)
    {
        text = text.Trim();
        if (text.Length > 128) text = text[..127] + "…";
        return text.Length < 2 ? text.PadRight(2, '\u00A0') : text;
    }

    private static bool ValidImage(string url) => url.Length is > 0 and <= 256 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Описание активности для SET_ACTIVITY; null — ничего не показывать (и снять статус).</summary>
    internal static JsonObject? BuildActivity(Track track, Options options, long nowSeconds)
    {
        if (!track.Playable || (!track.Playing && !options.ShowPaused)) return null;
        var service = track.Service == "youtube" ? "YouTube Music" : "Яндекс Музыка";
        var artist = track.Artist.Length > 0 ? track.Artist : service;
        var activity = new JsonObject
        {
            ["type"] = 2,                                   // Listening: «Слушает …»
            ["details"] = Clip(track.Title),
            ["state"] = Clip(track.Playing ? artist : "Пауза · " + artist)
        };
        if (track.Playing)
        {
            var start = nowSeconds - (long)Math.Floor(track.Position);
            var timestamps = new JsonObject { ["start"] = start };
            if (track.Duration > 0 && track.Position <= track.Duration + 1) timestamps["end"] = start + (long)Math.Ceiling(track.Duration);
            activity["timestamps"] = timestamps;            // с «end» Discord рисует полоску прогресса
        }
        if (options.Artwork && ValidImage(track.Artwork))
            activity["assets"] = new JsonObject { ["large_image"] = track.Artwork, ["large_text"] = Clip(track.Album.Length > 0 ? track.Album : service) };
        return activity;
    }

    internal static byte[] EncodeFrame(int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        return frame;
    }

    // ---------- вызывается из окна приложения ----------
    public void Configure(JsonNode? value) { var parsed = ParseOptions(value); lock (sync) options = parsed; Wake(); }
    public void Update(JsonObject snapshot) { var parsed = ParseTrack(snapshot); lock (sync) track = parsed; Wake(); }
    private void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    private void SetStatus(string next, string text = "", string name = "", bool shown = false) { lock (sync) { status = next; detail = text; user = name; active = shown; } }
    public JsonObject Status() { lock (sync) return new JsonObject { ["status"] = status, ["detail"] = detail, ["user"] = user, ["active"] = active }; }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        stop.Dispose();
    }

    // ---------- фоновый цикл ----------
    private async Task RunAsync(CancellationToken token)
    {
        Connection? connection = null;
        var nextConnect = DateTime.MinValue; var lastSent = DateTime.MinValue; var lastKey = ""; long lastStart = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await wake.WaitAsync(TimeSpan.FromSeconds(1), token); } catch (OperationCanceledException) { break; }
                while (wake.Wait(0)) { }
                Options o; Track t;
                lock (sync) { o = options; t = track; }
                try
                {
                    if (!o.Enabled || o.ClientId.Length == 0)
                    {
                        if (connection != null) { await CloseAsync(connection, true); connection = null; }
                        SetStatus(!o.Enabled ? "disabled" : "no_client_id"); lastKey = ""; continue;
                    }
                    if (connection != null && (connection.Lost || connection.ClientId != o.ClientId)) { await CloseAsync(connection, false); connection = null; lastKey = ""; }
                    if (connection == null)
                    {
                        if (DateTime.UtcNow < nextConnect) continue;
                        try { connection = await Connection.ConnectAsync(o.ClientId, token); }
                        catch (InvalidOperationException error) { SetStatus("error", error.Message); nextConnect = DateTime.UtcNow.AddSeconds(30); continue; }
                        if (connection == null) { SetStatus("waiting", "Discord не найден"); nextConnect = DateTime.UtcNow.AddSeconds(10); continue; }
                        lastKey = "";
                    }
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var activity = BuildActivity(t, o, now);
                    var key = activity == null ? "" : t.Title + "\n" + t.Artist + "\n" + t.Playing + "\n" + o.Artwork + "\n" + t.Artwork;
                    var start = activity == null ? 0 : now - (long)Math.Floor(t.Position);
                    var changed = key != lastKey || (activity != null && t.Playing && Math.Abs(start - lastStart) > 3);   // смена трека/паузы или перемотка
                    if (changed && DateTime.UtcNow - lastSent >= TimeSpan.FromSeconds(5))                                  // лимит Discord на обновления
                    {
                        await connection.SetActivityAsync(activity, token);
                        lastSent = DateTime.UtcNow; lastKey = key; lastStart = start;
                    }
                    SetStatus("connected", "", connection.User, lastKey.Length > 0);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    AppPaths.Log("Discord presence: " + error.Message);
                    if (connection != null) connection.Lost = true;
                    SetStatus("waiting", "Связь с Discord потеряна"); nextConnect = DateTime.UtcNow.AddSeconds(10);
                }
            }
        }
        finally { if (connection != null) await CloseAsync(connection, true); }
    }

    private static async Task CloseAsync(Connection connection, bool clear)
    {
        if (clear && !connection.Lost)
        {
            try { using var timeout = new CancellationTokenSource(1000); await connection.SetActivityAsync(null, timeout.Token); } catch { }
        }
        await connection.DisposeAsync();
    }

    /// <summary>Одно подключение к клиенту Discord: рукопожатие, SET_ACTIVITY и фоновое чтение (PING/закрытие).</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly NamedPipeClientStream pipe;
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly CancellationTokenSource cancel = new();
        private Task? reader;
        public string ClientId { get; }
        public string User { get; private set; } = "";
        public volatile bool Lost;

        private Connection(NamedPipeClientStream pipe, string clientId) { this.pipe = pipe; ClientId = clientId; }

        public static async Task<Connection?> ConnectAsync(string clientId, CancellationToken token)
        {
            for (var index = 0; index < 10; index++)
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + index, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(150, token);
                    var connection = new Connection(pipe, clientId);
                    await connection.HandshakeAsync(token);
                    connection.reader = Task.Run(connection.ReadLoopAsync);
                    return connection;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { pipe.Dispose(); throw; }
                catch (TimeoutException) { pipe.Dispose(); }
                catch (IOException) { pipe.Dispose(); }
                catch { pipe.Dispose(); throw; }
            }
            return null;
        }

        private async Task HandshakeAsync(CancellationToken token)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(4));
            await WriteAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = ClientId }.ToJsonString(), limit.Token);
            var frame = await ReadFrameAsync(limit.Token) ?? throw new IOException("Discord закрыл соединение");
            var message = JsonNode.Parse(frame.Json);
            if (frame.Opcode == 1 && message?["evt"]?.GetValue<string>() == "READY")
            {
                User = message["data"]?["user"]?["username"]?.GetValue<string>() ?? "";
                return;
            }
            throw new InvalidOperationException("Discord отклонил Application ID. Проверьте номер в настройках.");
        }

        public async Task SetActivityAsync(JsonObject? activity, CancellationToken token)
        {
            var payload = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
                ["nonce"] = Guid.NewGuid().ToString()
            };
            await WriteAsync(1, payload.ToJsonString(), token);
        }

        private async Task WriteAsync(int opcode, string json, CancellationToken token)
        {
            var frame = EncodeFrame(opcode, json);
            await writeLock.WaitAsync(token);
            try { await pipe.WriteAsync(frame, token); await pipe.FlushAsync(token); }
            finally { writeLock.Release(); }
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), token);
                if (count == 0) return false;
                read += count;
            }
            return true;
        }

        private async Task<(int Opcode, string Json)?> ReadFrameAsync(CancellationToken token)
        {
            var header = new byte[8];
            if (!await ReadExactAsync(pipe, header, token)) return null;
            var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (length < 0 || length > 65536) throw new InvalidDataException("Некорректный ответ Discord");
            var body = new byte[length];
            if (!await ReadExactAsync(pipe, body, token)) return null;
            return (opcode, Encoding.UTF8.GetString(body));
        }

        // Ответы на SET_ACTIVITY нужно вычитывать, иначе буфер канала переполнится; PING отвечаем PONG.
        private async Task ReadLoopAsync()
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(cancel.Token);
                    if (frame == null || frame.Value.Opcode == 2) { Lost = true; return; }
                    if (frame.Value.Opcode == 3) await WriteAsync(4, frame.Value.Json, cancel.Token);
                }
            }
            catch { Lost = true; }
        }

        public async ValueTask DisposeAsync()
        {
            cancel.Cancel();
            pipe.Dispose();
            if (reader != null) { try { await reader.WaitAsync(TimeSpan.FromSeconds(1)); } catch { } }
            cancel.Dispose();
        }
    }
}
