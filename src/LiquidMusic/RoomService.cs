using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiquidMusic;

internal enum RoomRole { None, Leader, Guest }

/// <summary>Что слышит лидер: только описание трека и состояние. Звук между людьми не передаётся —
/// у каждого играет свой сервис по своему аккаунту.</summary>
internal sealed record RoomTrack(string Title, string Artist, string Album, double Duration, double Position, bool Playing, string Service, long Seq)
{
    internal string Key => MusicNames.Normalize(Title) + "|" + MusicNames.Normalize(Artist);
    internal string Query => (Artist + " " + Title).Trim();
    internal JsonObject ToJson() => new()
    {
        ["t"] = "track", ["title"] = Title, ["artist"] = Artist, ["album"] = Album, ["duration"] = Duration,
        ["position"] = Position, ["playing"] = Playing, ["service"] = Service, ["seq"] = Seq
    };
    internal static RoomTrack? From(JsonObject message)
    {
        static string Text(JsonNode? node, int max = 200)
        {
            var value = node is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim() : "";
            return value.Length > max ? value[..max] : value;
        }
        static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var value) && double.IsFinite(value) ? Math.Clamp(value, 0, 86400) : 0;
        var title = Text(message["title"]);
        if (title.Length == 0) return null;
        return new(title, Text(message["artist"]), Text(message["album"]), Number(message["duration"]), Number(message["position"]),
            message["playing"] is JsonValue p && p.TryGetValue<bool>(out var playing) && playing, Text(message["service"], 24),
            message["seq"] is JsonValue s && s.TryGetValue<long>(out var seq) ? seq : 0);
    }
}

/// <summary>
/// Совместное прослушивание на двоих. Лидер поднимает локальный WebSocket-сервер, гость подключается по адресу и коду.
/// Управлять треками может только лидер: гость умеет лишь слушать, а его сообщения после приветствия игнорируются.
/// </summary>
internal sealed class RoomService : IAsyncDisposable
{
    internal const int Port = 8788, MaxGuests = 1;
    private const string Path = "/room";
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(4);
    private readonly object sync = new();
    private readonly List<WebSocket> guests = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private WebApplication? application;
    private ClientWebSocket? client;
    private CancellationTokenSource? cancellation;
    private RoomTrack? lastTrack;
    private DateTime lastSent;
    private long sequence;
    private string[] addressList = [];
    private string code = "", leaderName = "", error = "", peerName = "", address = "";
    internal RoomRole Role { get; private set; }
    internal event Action? Changed;
    internal event Action<RoomTrack>? TrackReceived;

    internal JsonObject Status()
    {
        lock (sync)
        {
            var result = new JsonObject
            {
                ["ok"] = true, ["role"] = Role.ToString().ToLowerInvariant(), ["error"] = error, ["peer"] = peerName,
                ["guests"] = guests.Count, ["max_guests"] = MaxGuests
            };
            if (Role == RoomRole.Leader)
            {
                result["code"] = code;
                result["addresses"] = new JsonArray(addressList.Select(a => (JsonNode)JsonValue.Create(a + ":" + Port + "/" + code)!).ToArray());
            }
            if (Role == RoomRole.Guest)
            {
                result["leader"] = leaderName; result["address"] = address;
                if (lastTrack != null) result["track"] = new JsonObject { ["title"] = lastTrack.Title, ["artist"] = lastTrack.Artist, ["playing"] = lastTrack.Playing };
            }
            return result;
        }
    }

    // ---------- лидер ----------
    internal async Task<JsonObject> CreateAsync(string name)
    {
        if (Role != RoomRole.None) throw new InvalidOperationException("Вы уже в комнате. Сначала выйдите из неё.");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(Port); options.Limits.MaxRequestBodySize = 1024; });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map(Path, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await HandleGuestAsync(socket, context.RequestAborted);
        });
        try { await app.StartAsync(); }
        catch (Exception failure)
        {
            await app.DisposeAsync();
            throw new InvalidOperationException("Не удалось открыть комнату: порт " + Port + " занят или закрыт брандмауэром. " + failure.Message, failure);
        }
        lock (sync)
        {
            application = app; Role = RoomRole.Leader; leaderName = CleanName(name, "Лидер"); error = "";
            code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(); peerName = ""; lastTrack = null; sequence = 0;
            addressList = Addresses().ToArray();
        }
        cancellation = new();
        _ = Task.Run(() => HeartbeatLoopAsync(cancellation.Token));
        Changed?.Invoke();
        return Status();
    }

    private async Task HandleGuestAsync(WebSocket socket, CancellationToken token)
    {
        string guest;
        try
        {
            using var hello = CancellationTokenSource.CreateLinkedTokenSource(token);
            hello.CancelAfter(TimeSpan.FromSeconds(5));
            var message = await ReceiveAsync(socket, hello.Token);
            var expected = Encoding.UTF8.GetBytes(code);
            var given = Encoding.UTF8.GetBytes(message?["code"]?.GetValue<string>() ?? "");
            if (message?["t"]?.GetValue<string>() != "hello" || !CryptographicOperations.FixedTimeEquals(expected, given))
            { await SendAsync(socket, new() { ["t"] = "deny", ["reason"] = "Неверный код комнаты." }, token); return; }
            guest = CleanName(message["name"]?.GetValue<string>(), "Гость");
            bool full;
            lock (sync)
            {
                full = guests.Count >= MaxGuests;
                if (!full) { guests.Add(socket); peerName = guest; }
            }
            if (full) { await SendAsync(socket, new() { ["t"] = "deny", ["reason"] = "В комнате уже есть слушатель." }, token); return; }
        }
        catch (Exception failure) when (failure is OperationCanceledException or WebSocketException or JsonException or InvalidOperationException) { return; }
        Changed?.Invoke();
        try
        {
            await SendAsync(socket, new() { ["t"] = "welcome", ["name"] = leaderName }, token);
            RoomTrack? current; lock (sync) current = lastTrack;
            if (current != null) await SendAsync(socket, current.ToJson(), token);
            // Дальше от гостя читаем только факт закрытия: любые его команды игнорируются.
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                if ((await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)).MessageType == WebSocketMessageType.Close) break;
        }
        catch (Exception failure) when (failure is OperationCanceledException or WebSocketException) { }
        finally
        {
            lock (sync) { guests.Remove(socket); if (guests.Count == 0) peerName = ""; }
            Changed?.Invoke();
        }
    }

    /// <summary>Вызывается при каждом обновлении состояния плеера лидера.</summary>
    internal void Publish(JsonObject snapshot)
    {
        if (Role != RoomRole.Leader) return;
        var title = snapshot["title"]?.GetValue<string>()?.Trim() ?? "";
        if (snapshot["connected"]?.GetValue<bool>() != true || title.Length == 0 || title == "В ожидании музыки") return;
        double Num(string key) => snapshot[key] is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n) ? n : 0;
        var track = new RoomTrack(title, snapshot["artist"]?.GetValue<string>() ?? "", snapshot["album"]?.GetValue<string>() ?? "", Num("duration"), Num("position"),
            snapshot["playing"]?.GetValue<bool>() == true, snapshot["service"]?.GetValue<string>() ?? "", 0);
        bool send;
        lock (sync)
        {
            send = lastTrack == null || lastTrack.Key != track.Key || lastTrack.Playing != track.Playing || DateTime.UtcNow - lastSent > Heartbeat;
            if (!send) return;
            track = track with { Seq = ++sequence }; lastTrack = track; lastSent = DateTime.UtcNow;
        }
        _ = BroadcastAsync(track.ToJson());
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(Heartbeat);
            while (await timer.WaitForNextTickAsync(token))
            {
                RoomTrack? track; lock (sync) track = lastTrack;
                if (track != null && DateTime.UtcNow - lastSent > Heartbeat) { lock (sync) { lastSent = DateTime.UtcNow; } await BroadcastAsync(track.ToJson()); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task BroadcastAsync(JsonObject message)
    {
        WebSocket[] targets; lock (sync) targets = guests.ToArray();
        foreach (var socket in targets)
            try { await SendAsync(socket, message, CancellationToken.None); } catch (Exception failure) when (failure is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    // ---------- гость ----------
    /// <summary>invite: «адрес:порт/код» или «адрес/код».</summary>
    internal async Task<JsonObject> JoinAsync(string invite, string name)
    {
        if (Role != RoomRole.None) throw new InvalidOperationException("Вы уже в комнате. Сначала выйдите из неё.");
        var slash = invite.Trim().LastIndexOf('/');
        if (slash <= 0 || slash == invite.Trim().Length - 1) throw new ArgumentException("Вставьте приглашение вида 192.168.1.5:8788/123456");
        var host = invite.Trim()[..slash]; var roomCode = invite.Trim()[(slash + 1)..];
        if (roomCode.Length != 6 || !roomCode.All(char.IsAsciiDigit)) throw new ArgumentException("Код комнаты — 6 цифр.");
        if (!host.Contains(':')) host += ":" + Port;
        if (!Uri.TryCreate("ws://" + host + Path, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port is <= 0 or > 65535) throw new ArgumentException("Не получилось прочитать адрес лидера.");
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await socket.ConnectAsync(uri, connect.Token);
            await SendAsync(socket, new() { ["t"] = "hello", ["code"] = roomCode, ["name"] = CleanName(name, "Гость") }, connect.Token);
            var answer = await ReceiveAsync(socket, connect.Token);
            if (answer?["t"]?.GetValue<string>() != "welcome")
                throw new InvalidOperationException(answer?["reason"]?.GetValue<string>() ?? "Лидер не принял подключение.");
            lock (sync) { client = socket; Role = RoomRole.Guest; leaderName = CleanName(answer["name"]?.GetValue<string>(), "Лидер"); address = host; error = ""; lastTrack = null; }
        }
        catch (Exception failure) when (failure is OperationCanceledException or WebSocketException or JsonException)
        {
            socket.Dispose();
            throw new InvalidOperationException("Не удалось подключиться к комнате. Проверьте адрес, код и что лидер создал комнату.", failure);
        }
        catch { socket.Dispose(); throw; }
        cancellation = new();
        _ = Task.Run(() => GuestLoopAsync(socket, cancellation.Token));
        Changed?.Invoke();
        return Status();
    }

    private async Task GuestLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, token);
                if (message == null) break;
                if (message["t"]?.GetValue<string>() != "track" || RoomTrack.From(message) is not { } track) continue;
                lock (sync) { if (lastTrack != null && track.Seq != 0 && track.Seq < lastTrack.Seq) continue; lastTrack = track; }
                Changed?.Invoke();
                TrackReceived?.Invoke(track);
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or WebSocketException or JsonException or InvalidOperationException) { }
        bool dropped;
        lock (sync) dropped = Role == RoomRole.Guest && ReferenceEquals(client, socket);
        if (dropped) { lock (sync) { error = "Связь с лидером потеряна."; } await LeaveAsync(); }
    }

    internal async Task LeaveAsync()
    {
        WebApplication? app; ClientWebSocket? socket; WebSocket[] open;
        lock (sync)
        {
            app = application; socket = client; open = guests.ToArray();
            application = null; client = null; guests.Clear(); peerName = ""; Role = RoomRole.None; lastTrack = null;
        }
        cancellation?.Cancel(); cancellation = null;
        if (socket != null) { try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { } socket.Dispose(); }
        foreach (var guest in open) try { await guest.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "room closed", CancellationToken.None); } catch { }
        if (app != null) try { await app.StopAsync(TimeSpan.FromSeconds(2)); await app.DisposeAsync(); } catch { }
        Changed?.Invoke();
    }

    // ---------- общее ----------
    private static string CleanName(string? value, string fallback)
    {
        var name = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? fallback : name.Length > 24 ? name[..24] : name;
    }

    private async Task SendAsync(WebSocket socket, JsonObject message, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await sendGate.WaitAsync(token);
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token); }
        finally { sendGate.Release(); }
    }

    private static async Task<JsonObject?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096]; using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 4096) throw new InvalidOperationException("Слишком большое сообщение комнаты.");
        } while (!result.EndOfMessage);
        return JsonNode.Parse(stream.ToArray()) as JsonObject;
    }

    private static IEnumerable<string> Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString()).Distinct().Take(4);

    public async ValueTask DisposeAsync() => await LeaveAsync();
}
