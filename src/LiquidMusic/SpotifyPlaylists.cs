using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LiquidMusic;

internal static class SpotifyPlaylists
{
    internal static string NormalizeUrl(string input)
    {
        if(input.Length>2048)throw new ArgumentException("Ссылка слишком длинная.");
        if(!Uri.TryCreate(input.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="open.spotify.com"||!uri.IsDefaultPort||uri.UserInfo.Length>0)
            throw new ArgumentException("Вставьте ссылку вида https://open.spotify.com/playlist/…");
        var match=Regex.Match(uri.AbsolutePath,@"^/(?:intl-[a-zA-Z-]+/)?playlist/([a-zA-Z0-9]{22})/?$");
        if(!match.Success)throw new ArgumentException("Нужна ссылка на плейлист Spotify, а не на трек или альбом.");
        return "https://open.spotify.com/playlist/"+match.Groups[1].Value;
    }
    internal static JsonArray Saved(Settings settings,string account)=>settings.Value["spotify_playlists"]?[account] is JsonArray list?(JsonArray)list.DeepClone():new();
    internal static JsonArray Merge(params JsonArray[] sources)
    {
        var entries=new Dictionary<string,JsonObject>();
        foreach(var source in sources)foreach(var node in source.OfType<JsonObject>())
        {
            try
            {
                var url=NormalizeUrl(node["url"]?.GetValue<string>()??"");
                if(entries.ContainsKey(url))continue;
                var title=node["title"]?.GetValue<string>()?.Trim();if(string.IsNullOrEmpty(title))continue;
                var artwork=node["artwork"]?.GetValue<string>()??"";
                entries[url]=new(){["id"]=url.Split('/')[^1],["url"]=url,["title"]=title.Length>200?title[..200]:title,
                    ["artwork"]=Uri.TryCreate(artwork,UriKind.Absolute,out var image)&&image.Scheme=="https"?artwork:""};
            }
            catch(ArgumentException){}
        }
        return new JsonArray(entries.Values.Select(entry=>(JsonNode)entry).ToArray());
    }
    internal static bool Save(Settings settings,string account,JsonObject entry)
    {
        if(string.IsNullOrWhiteSpace(account)||account.Length>256)throw new ArgumentException("Не удалось определить аккаунт Spotify.");
        var saved=Saved(settings,account);
        var merged=Merge(saved,new JsonArray(entry.DeepClone()));
        if(merged.Count==saved.Count)return false;
        if(merged.Count>200)throw new InvalidOperationException("В виджете уже добавлено 200 плейлистов.");
        if(settings.Value["spotify_playlists"] is not JsonObject)settings.Value["spotify_playlists"]=new JsonObject();
        settings.Value["spotify_playlists"]![account]=merged;settings.Save();return true;
    }
    internal static void UpdateDetails(Settings settings,string account,string url,string title,string artwork)
    {
        if(string.IsNullOrWhiteSpace(title))return;
        var saved=Saved(settings,account);
        var existing=saved.OfType<JsonObject>().FirstOrDefault(entry=>entry["url"]?.GetValue<string>()==url);
        if(existing==null)return;
        if(string.IsNullOrWhiteSpace(artwork))artwork=existing["artwork"]?.GetValue<string>()??"";
        settings.Value["spotify_playlists"]![account]=Merge(new JsonArray(new JsonObject{["url"]=url,["title"]=title,["artwork"]=artwork}),saved);
        settings.Save();
    }
}
