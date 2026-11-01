using System.Text.Json.Nodes;

namespace LiquidMusic;

internal static class DiscordPresenceTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    public static void Run()
    {
        Check(DiscordPresence.ValidClientId("123456789012345678") && !DiscordPresence.ValidClientId("123") && !DiscordPresence.ValidClientId("12345678901234567x"), "Application ID validation");
        Check(DiscordPresence.Clip("a").Length == 2 && DiscordPresence.Clip(new string('x', 300)).Length == 128, "Discord string length limits");

        var options = DiscordPresence.ParseOptions(JsonNode.Parse("""{"enabled":true,"client_id":"123456789012345678","show_artwork":true,"show_paused":false}"""));
        Check(options.Enabled && options.ClientId == "123456789012345678", "Options parsing");
        Check(DiscordPresence.ParseOptions(JsonNode.Parse("""{"client_id":"bad"}""")).ClientId == (DiscordPresence.HasDefaultClientId ? DiscordPresence.DefaultClientId : ""), "Invalid Application ID falls back to the built-in one");

        var snapshot = JsonNode.Parse("""{"connected":true,"title":"Династия","artist":"VILLIAN","album":"Альбом","artwork":"https://avatars.yandex.net/get-music-content/1/400x400","playing":true,"position":30.4,"duration":200.2,"service":"yandex"}""")!.AsObject();
        var activity = DiscordPresence.BuildActivity(DiscordPresence.ParseTrack(snapshot), options, 1_000_000)!;
        Check(activity["type"]!.GetValue<int>() == 2 && activity["details"]!.GetValue<string>() == "Династия" && activity["state"]!.GetValue<string>() == "VILLIAN", "Listening activity text");
        Check(activity["timestamps"]!["start"]!.GetValue<long>() == 1_000_000 - 30 && activity["timestamps"]!["end"]!.GetValue<long>() == 1_000_000 - 30 + 201, "Progress timestamps");
        Check(activity["assets"]!["large_image"]!.GetValue<string>().StartsWith("https://"), "Artwork URL");

        var noArtwork = DiscordPresence.BuildActivity(DiscordPresence.ParseTrack(snapshot), options with { Artwork = false }, 1_000_000)!;
        Check(noArtwork["assets"] == null, "Artwork can be switched off");
        snapshot["artwork"] = "http://insecure.example/x.jpg";
        Check(DiscordPresence.BuildActivity(DiscordPresence.ParseTrack(snapshot), options, 1)!["assets"] == null, "Only https artwork is sent");

        snapshot["playing"] = false;
        Check(DiscordPresence.BuildActivity(DiscordPresence.ParseTrack(snapshot), options, 1) == null, "Paused track is hidden by default");
        Check(DiscordPresence.BuildActivity(DiscordPresence.ParseTrack(snapshot), options with { ShowPaused = true }, 1)!["timestamps"] == null, "Paused track has no running timer");
        Check(DiscordPresence.BuildActivity(DiscordPresence.ParseTrack(JsonNode.Parse("""{"connected":true,"title":"В ожидании музыки","playing":true}""")!.AsObject()), options, 1) == null, "Placeholder title is never shown");

        var frame = DiscordPresence.EncodeFrame(1, "{}");
        Check(frame.Length == 10 && frame[0] == 1 && frame[4] == 2 && frame[8] == (byte)'{', "Discord IPC frame layout");
    }
}
