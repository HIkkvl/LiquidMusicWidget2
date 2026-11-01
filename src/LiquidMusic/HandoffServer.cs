using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiquidMusic;

/// <summary>
/// Локальный канал между Liquid Music и телефоном. Не открывает интернет-сайтам
/// доступ к плееру: все рабочие endpoint'ы требуют код текущего запуска.
/// </summary>
internal sealed class HandoffServer : IAsyncDisposable
{
    private readonly Func<Task<JsonObject>> snapshotProvider;
    private readonly Func<string, Task<JsonObject>> commandHandler;
    private readonly int requestedPort;
    private readonly string pairingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
    private WebApplication? application;

    private static readonly HttpClient AudioHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public HandoffServer(
        Func<Task<JsonObject>> snapshotProvider,
        Func<string, Task<JsonObject>> commandHandler,
        int port = 8787)
    {
        this.snapshotProvider = snapshotProvider;
        this.commandHandler = commandHandler;
        requestedPort = port;
    }

    public int Port { get; private set; }
    public string? LastError { get; private set; }
    public bool Running => application != null && LastError == null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (application != null) return;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(requestedPort);
            options.Limits.MaxRequestBodySize = 4096;
        });

        var nextApplication = builder.Build();
        nextApplication.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/handoff") && !Authorized(context.Request))
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "Неверный код сопряжения.");
                return;
            }
            await next();
        });

        nextApplication.MapGet("/health", () => Results.Json(new
        {
            name = "Liquid Music Handoff",
            version = 2,
        }));
        nextApplication.MapGet("/handoff/state", async context =>
        {
            var state = HandoffState.From(await snapshotProvider());
            await WriteJson(context, state.PublicJson());
        });
        nextApplication.MapMethods("/handoff/audio", ["GET", "HEAD"], HandleAudioAsync);
        nextApplication.MapPost("/handoff/command", HandleCommandAsync);

        try
        {
            await nextApplication.StartAsync(cancellationToken);
            application = nextApplication;
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.FirstOrDefault();
            Port = address != null && Uri.TryCreate(address, UriKind.Absolute, out var uri)
                ? uri.Port
                : requestedPort;
            LastError = null;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            await nextApplication.DisposeAsync();
            throw;
        }
    }

    public JsonObject Status()
    {
        var addresses = LocalAddresses().Select(address => $"http://{address}:{Port}").ToArray();
        return new JsonObject
        {
            ["running"] = Running,
            ["pairing_key"] = pairingKey,
            ["port"] = Port,
            ["addresses"] = new JsonArray(addresses.Select(address => JsonValue.Create(address)).ToArray()),
            ["error"] = LastError ?? "",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (application == null) return;
        var current = application;
        application = null;
        await current.StopAsync();
        await current.DisposeAsync();
    }

    private bool Authorized(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = Encoding.UTF8.GetBytes(header[scheme.Length..]);
        var expected = Encoding.UTF8.GetBytes(pairingKey);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private async Task HandleCommandAsync(HttpContext context)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var action = document.RootElement.TryGetProperty("action", out var value)
                ? value.GetString()
                : null;
            if (action != "pause")
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "Поддерживается только команда pause.");
                return;
            }
            var state = HandoffState.From(await commandHandler(action));
            await WriteJson(context, state.PublicJson());
        }
        catch (JsonException)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Некорректная команда.");
        }
        catch (Exception error)
        {
            AppPaths.Log("Handoff command: " + error.Message);
            await WriteError(context, StatusCodes.Status409Conflict, error.Message);
        }
    }

    private async Task HandleAudioAsync(HttpContext context)
    {
        HttpResponseMessage? upstream = null;
        try
        {
            var state = HandoffState.From(await snapshotProvider());
            var requestedTrack = context.Request.Query["trackId"].ToString();
            if (state.TrackId == null)
            {
                await WriteError(context, StatusCodes.Status409Conflict, "В Liquid Music не выбран трек.");
                return;
            }
            if (requestedTrack.Length > 0 && requestedTrack != state.TrackId)
            {
                await WriteError(context, StatusCodes.Status409Conflict, "Трек на ПК изменился. Повторите перенос.");
                return;
            }
            if (state.StreamUrl == null)
            {
                await WriteError(context, StatusCodes.Status422UnprocessableEntity,
                    "Музыкальный сервис не отдал прямой аудиопоток. Для YouTube с blob-ссылкой перенос звука недоступен.");
                return;
            }

            using var request = new HttpRequestMessage(
                context.Request.Method == HttpMethods.Head ? HttpMethod.Head : HttpMethod.Get,
                state.StreamUrl);
            if (RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var range))
                request.Headers.Range = range;
            if (state.PageUrl != null) request.Headers.Referrer = new Uri(state.PageUrl);
            if (state.UserAgent.Length > 0) request.Headers.TryAddWithoutValidation("User-Agent", state.UserAgent);

            upstream = await AudioHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                await WriteError(context, StatusCodes.Status502BadGateway,
                    $"Аудиосервер ответил HTTP {(int)upstream.StatusCode}. Запустите трек на ПК ещё раз и повторите.");
                return;
            }

            context.Response.StatusCode = (int)upstream.StatusCode;
            CopyHeader(upstream, context.Response, "Content-Type");
            CopyHeader(upstream, context.Response, "Content-Length");
            CopyHeader(upstream, context.Response, "Content-Range");
            CopyHeader(upstream, context.Response, "Accept-Ranges");
            CopyHeader(upstream, context.Response, "Cache-Control");
            CopyHeader(upstream, context.Response, "ETag");
            CopyHeader(upstream, context.Response, "Last-Modified");

            if (context.Request.Method == HttpMethods.Head) return;
            await upstream.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Телефон закрыл поток или перешёл к другой позиции — это нормальное завершение.
        }
        catch (Exception error)
        {
            AppPaths.Log("Handoff audio: " + error.Message);
            if (!context.Response.HasStarted)
                await WriteError(context, StatusCodes.Status502BadGateway, "Не удалось открыть аудиопоток: " + error.Message);
            else
                context.Abort();
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private static void CopyHeader(HttpResponseMessage source, HttpResponse target, string name)
    {
        if (source.Headers.TryGetValues(name, out var general)) target.Headers[name] = general.ToArray();
        else if (source.Content.Headers.TryGetValues(name, out var content)) target.Headers[name] = content.ToArray();
    }

    private static IEnumerable<string> LocalAddresses()
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address.Address)) continue;
                    result.Add(address.Address.ToString());
                }
            }
        }
        catch { }
        return result;
    }

    private static Task WriteJson(HttpContext context, JsonObject value, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(value.ToJsonString(), context.RequestAborted);
    }

    private static Task WriteError(HttpContext context, int status, string message) =>
        WriteJson(context, new JsonObject { ["error"] = message }, status);
}

internal sealed class HandoffState
{
    public string? TrackId { get; private init; }
    public string? StreamUrl { get; private init; }
    public string? PageUrl { get; private init; }
    public string? TrackUrl { get; private init; }
    public string StreamKind { get; private init; } = "";
    public string UserAgent { get; private init; } = "";
    public string Title { get; private init; } = "";
    public string Artist { get; private init; } = "";
    public string Album { get; private init; } = "";
    public string Service { get; private init; } = "";
    public string? ArtworkUrl { get; private init; }
    public long? DurationMs { get; private init; }
    public long PositionMs { get; private init; }
    public bool IsPlaying { get; private init; }

    public static HandoffState From(JsonObject? source)
    {
        if (source?["connected"]?.GetValue<bool>() != true) return new HandoffState();
        var title = Text(source["title"]);
        if (title.Length == 0 || title == "В ожидании музыки" || title == "Подключение…") return new HandoffState();
        var artist = Text(source["artist"]);
        var service = Text(source["service"]);
        var trackUrl = HttpUrl(source["track_url"]);
        var pageUrl = HttpUrl(source["page_url"]);
        var streamUrl = HttpUrl(source["stream_url"]);
        var identity = Text(source["track_id"]);
        if (identity.Length == 0) identity = $"{trackUrl ?? pageUrl ?? service}\0{title}\0{artist}";
        var trackId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
        var duration = Seconds(source["duration"]);
        var position = Math.Max(0, Seconds(source["position"]) ?? 0);
        if (duration != null) position = Math.Min(position, duration.Value);

        return new HandoffState
        {
            TrackId = trackId,
            StreamUrl = streamUrl,
            PageUrl = pageUrl,
            TrackUrl = trackUrl,
            StreamKind = Text(source["stream_kind"]),
            UserAgent = Text(source["user_agent"]),
            Title = title,
            Artist = artist,
            Album = Text(source["album"]),
            Service = service,
            ArtworkUrl = HttpUrl(source["artwork"]),
            DurationMs = duration is > 0 ? duration : null,
            PositionMs = position,
            IsPlaying = source["playing"]?.GetValue<bool>() == true,
        };
    }

    public JsonObject PublicJson() => new()
    {
        ["trackId"] = TrackId,
        ["title"] = Title,
        ["artist"] = Artist,
        ["album"] = Album,
        ["service"] = Service,
        ["artworkUrl"] = ArtworkUrl,
        ["durationMs"] = DurationMs,
        ["positionMs"] = PositionMs,
        ["isPlaying"] = IsPlaying,
        ["audioAvailable"] = StreamUrl != null,
        // Если прямого звука нет (сервис играет через MediaSource/blob), телефон может открыть трек по ссылке в официальном приложении или на сайте.
        ["audioReason"] = StreamUrl != null ? null : StreamKind == "blob"
            ? "Сервис отдаёт звук через MediaSource (blob:), прямой ссылки нет. Откройте трек по trackUrl."
            : "Источник звука на странице не найден.",
        ["streamKind"] = StreamKind,
        ["trackUrl"] = TrackUrl ?? PageUrl,
        ["pageUrl"] = PageUrl,
        ["serverTimeMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static string Text(JsonNode? value)
    {
        if (value is not JsonValue item) return "";
        if (item.TryGetValue<string>(out var text)) return text.Trim();
        if (item.TryGetValue<long>(out var number)) return number.ToString();
        return "";
    }

    private static long? Seconds(JsonNode? value)
    {
        if (value is not JsonValue item || !item.TryGetValue<double>(out var seconds) || !double.IsFinite(seconds)) return null;
        return (long)Math.Max(0, Math.Round(seconds * 1000));
    }

    private static string? HttpUrl(JsonNode? value)
    {
        var candidate = Text(value);
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : null;
    }
}
