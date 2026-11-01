using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace LiquidMusic;

internal sealed partial class BrowserController
{
    private readonly Dictionary<string,JsonArray> spotifyCatalog=new();
    public async Task<JsonObject> SpotifyLibraryAsync(string? addUrl=null)
    {
        var normalized=addUrl==null?null:SpotifyPlaylists.NormalizeUrl(addUrl);
        await gate.WaitAsync();ICDPSession? focus=null;
        try
        {
            if(settings.Service!="spotify")throw new InvalidOperationException("Сначала выберите Spotify.");
            var page=await PageAsync("spotify",true);
            if((await ReadAsync(page,"spotify"))["signed_in"]?.GetValue<bool>()!=true)throw new InvalidOperationException("Войдите в Spotify: Настройки → Аккаунт.");
            focus=await page.Context.NewCDPSessionAsync(page);
            await focus.SendAsync("Emulation.setFocusEmulationEnabled",new Dictionary<string,object>{{"enabled",true}});
            var account=await SpotifyAccountAsync(page,script);
            async Task<JsonArray> Read()=>JsonNode.Parse((await page.EvaluateAsync<JsonElement>(script,new{action="spotify_library",service="spotify"})).GetRawText())!.AsArray();
            var discovered=await Read();
            var added=false;
            if(normalized!=null)
            {
                var known=SpotifyPlaylists.Merge(discovered,spotifyCatalog.GetValueOrDefault(account)??new(),SpotifyPlaylists.Saved(settings,account));
                var entry=known.OfType<JsonObject>().FirstOrDefault(item=>item["url"]?.GetValue<string>()==normalized);
                if(entry==null)
                {
                    var metadata=await page.EvaluateAsync<JsonElement>("""
                        async url=>{
                          try{
                          const response=await fetch('/oembed?url='+encodeURIComponent(url),{signal:AbortSignal.timeout(10000)});
                          if(!response.ok)return {error:true};
                          const data=await response.json();return {title:data.title,artwork:data.thumbnail_url||''};
                          }catch{return {error:true};}
                        }
                        """,normalized);
                    // Private playlists may not expose public oEmbed metadata.
                    // Keep their link; the authenticated player can still open
                    // it and fill in the actual name after playback starts.
                    entry=!metadata.TryGetProperty("error",out _)&&metadata.TryGetProperty("title",out var title)&&title.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(title.GetString())?
                        new(){["url"]=normalized,["title"]=title.GetString(),["artwork"]=metadata.GetProperty("artwork").GetString()}:
                        new(){["url"]=normalized,["title"]="Плейлист Spotify · "+normalized[^6..],["artwork"]=""};
                }
                added=SpotifyPlaylists.Save(settings,account,entry);
            }
            else
            {
                // Follow the real Home button: SPA navigation keeps audio alive.
                if(new Uri(page.Url).AbsolutePath.Trim('/') is not ("" or "intl-ru"))
                {
                    var home=await page.EvaluateAsync<bool>(script,new{action="spotify_home_target",service="spotify"});
                    if(home)await ClickControlAsync(page,"spotify-home");
                }
                var stable=0;var lastCount=discovered.Count;
                for(var i=0;i<30;i++)
                {
                    await Task.Delay(200);
                    discovered=SpotifyPlaylists.Merge(discovered,await Read());
                    stable=discovered.Count==lastCount?stable+1:0;lastCount=discovered.Count;
                    if(discovered.Count>0&&i>=8&&stable>=3)break;
                }
                try
                {
                    for(var i=0;i<30;i++)
                    {
                        var moved=await page.EvaluateAsync<bool>(script,new{action="spotify_library_scroll",service="spotify"});
                        if(!moved)break;
                        await Task.Delay(180);discovered=SpotifyPlaylists.Merge(discovered,await Read());
                    }
                }
                finally{await page.EvaluateAsync<JsonElement>(script,new{action="spotify_library_restore",service="spotify"});}
                spotifyCatalog[account]=discovered;
            }
            var playlists=SpotifyPlaylists.Merge(SpotifyPlaylists.Saved(settings,account),discovered,spotifyCatalog.GetValueOrDefault(account)??new());
            return new(){["ok"]=true,["account"]=account,["playlists"]=playlists,["added"]=added};
        }
        finally
        {
            if(focus!=null)try{await focus.SendAsync("Emulation.setFocusEmulationEnabled",new Dictionary<string,object>{{"enabled",false}});await focus.DetachAsync();}catch(PlaywrightException){}
            gate.Release();
        }
    }
    internal static async Task<string> SpotifyAccountAsync(IPage page,string script)
    {
        var opened=false;
        try
        {
            for(var i=0;i<15;i++)
            {
                var info=await page.EvaluateAsync<JsonElement>(script,new{action="spotify_account",service="spotify"});
                if(info.TryGetProperty("account",out var account)&&account.ValueKind==JsonValueKind.String)return account.GetString()!;
                if(!opened&&info.GetProperty("can_open").GetBoolean()){opened=await ClickControlAsync(page,"spotify-account");}
                await Task.Delay(150);
            }
        }
        finally{if(opened)await page.Keyboard.PressAsync("Escape");}
        throw new InvalidOperationException("Не удалось определить аккаунт Spotify. Откройте профиль в окне Spotify, затем повторите.");
    }
    internal static async Task<JsonObject> StartSpotifyPlaylistAsync(IPage page,string script,string url)
    {
        await page.GotoAsync(url,new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=30000});
        var clicked=false;
        for(var i=0;i<60;i++)
        {
            var target=await page.EvaluateAsync<JsonElement>(script,new{action="spotify_playlist_target",service="spotify"});
            if((target.TryGetProperty("already",out var already)&&already.GetBoolean())||
                (!clicked&&target.GetProperty("found").GetBoolean()&&await ClickControlAsync(page,"spotify-playlist")))clicked=true;
            if(clicked)
            {
                var state=JsonNode.Parse((await page.EvaluateAsync<JsonElement>(script,new{action="snapshot",service="spotify"})).GetRawText())!.AsObject();
                if(state["playing"]?.GetValue<bool>()==true)
                {
                    await page.EvaluateAsync<JsonElement>(script,new{action="spotify_playlist_selected",service="spotify",query=url.Split('/')[^1]});
                    var details=await page.EvaluateAsync<JsonElement>(script,new{action="spotify_playlist_details",service="spotify"});
                    state["spotify_playlist_title"]=details.GetProperty("title").GetString();
                    state["spotify_playlist_artwork"]=details.GetProperty("artwork").GetString();
                    state["active_spotify_playlist"]=url.Split('/')[^1];return state;
                }
            }
            await Task.Delay(250);
        }
        throw new InvalidOperationException("Плейлист не начал играть. Проверьте доступ к нему, подписку Spotify и закройте диалоги в окне сервиса.");
    }
}
