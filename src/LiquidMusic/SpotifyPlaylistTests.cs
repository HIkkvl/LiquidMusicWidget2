using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace LiquidMusic;

internal static class SpotifyPlaylistTests
{
    private const string Id="37i9dQZF1DXcBWIGoYBM5M",AddedId="0123456789ABCDEFGHIJKL",PrivateId="1234567890ABCDEFGHIJKL";
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    internal static void Run()
    {
        Check(SpotifyPlaylists.NormalizeUrl("https://open.spotify.com/intl-ru/playlist/"+Id+"?si=shared#x")=="https://open.spotify.com/playlist/"+Id,"Normalize localized shared playlist links");
        foreach(var bad in new[]{"http://open.spotify.com/playlist/"+Id,"https://open.spotify.com.evil.example/playlist/"+Id,"https://open.spotify.com/track/"+Id,"https://example.com/playlist/"+Id,"https://open.spotify.com:444/playlist/"+Id,"https://open.spotify.com/playlist/../track/"+Id})
        {
            var rejected=false;try{SpotifyPlaylists.NormalizeUrl(bad);}catch(ArgumentException){rejected=true;}Check(rejected,"Reject non-playlist or unsafe link");
        }
        var root=Path.Combine(AppPaths.Data,"playlist-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var file=Path.Combine(root,"settings.json");var settings=new Settings(file);
        var entry=new JsonObject{["url"]="https://open.spotify.com/playlist/"+Id,["title"]="Private mix",["artwork"]="https://example.test/art.png"};
        Check(SpotifyPlaylists.Save(settings,"account-a",entry),"Add playlist to account");
        Check(!SpotifyPlaylists.Save(settings,"account-a",entry),"Do not duplicate imported playlist");
        Check(SpotifyPlaylists.Saved(new Settings(file),"account-a").Count==1,"Imported playlist survives restart");
        Check(SpotifyPlaylists.Saved(settings,"account-b").Count==0,"Imported playlists stay private to selected account");
        SpotifyPlaylists.UpdateDetails(settings,"account-a","https://open.spotify.com/playlist/"+Id,"Updated title","");
        Check(SpotifyPlaylists.Saved(settings,"account-a")[0]!["artwork"]?.GetValue<string>()=="https://example.test/art.png","Updating playlist title preserves a known cover");
    }
    internal static async Task CheckIntegrationAsync(SpotifyMusicHost host,IPage page,string root)
    {
        await page.UnrouteAllAsync();
        var fixture=ServicePlayerTests.Fixture("spotify")+$$"""
            <button aria-label='Главная' id='home'>Home</button>
            <aside id='Desktop_LeftSidebar_Id'><div style='height:80px;overflow-y:auto' id='library-scroll'>
              <div data-testid='card'><a href='/playlist/{{Id}}'>Daily Mix 1</a><img src='https://example.test/mix.png'></div>
              <div data-testid='card'><a href='/intl-ru/playlist/{{Id}}?si=duplicate'>Daily Mix 1</a></div>
              <div style='height:200px'><a href='/album/37i9dQZF1DXcBWIGoYBM5M'>An album, not a playlist</a></div>
            </div></aside>
            <main><h1>Account playlist</h1><button data-testid='play-button' id='playlist-play' aria-label='Включить плейлист'>Play playlist</button></main>
            <script>
            window.accountId='account-a';const avatar=document.querySelector('[data-testid="user-widget-link"]');
            avatar.addEventListener('click',event=>{if(!event.isTrusted)return;document.querySelector('[role=menu]')?.remove();const menu=document.createElement('div');menu.setAttribute('role','menu');menu.innerHTML='<a href="/user/'+window.accountId+'">Profile</a>';document.body.append(menu);});
            document.addEventListener('keydown',event=>{if(event.key==='Escape')document.querySelector('[role=menu]')?.remove();});
            document.getElementById('home').addEventListener('click',()=>history.pushState({},'','/'));
            document.getElementById('playlist-play').addEventListener('click',async event=>{if(!event.isTrusted)return;await audio.play();document.getElementById('title').textContent='Playlist track';document.getElementById('play').setAttribute('aria-label','Pause');});
            navigator.mediaSession.setActionHandler('pause',()=>{window.trustedActions.push('media:pause');audio.pause();document.getElementById('play').setAttribute('aria-label','Play');});
            navigator.mediaSession.setActionHandler('play',async()=>{window.trustedActions.push('media:play');await audio.play();document.getElementById('play').setAttribute('aria-label','Pause');});
            navigator.mediaSession.setActionHandler('nexttrack',()=>{window.trustedActions.push('media:next');document.getElementById('title').textContent='Next playlist track';audio.currentTime=0;});
            navigator.mediaSession.setActionHandler('previoustrack',()=>{window.trustedActions.push('media:previous');document.getElementById('title').textContent='Previous playlist track';audio.currentTime=0;});
            </script>
            """;
        await page.RouteAsync("**/*",route=>new Uri(route.Request.Url).AbsolutePath=="/oembed"?
            route.FulfillAsync(new(){Status=route.Request.Url.Contains(PrivateId)?404:200,ContentType="application/json",Body="{\"title\":\"Added playlist\",\"thumbnail_url\":\"https://example.test/added.png\"}"}):
            route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=fixture}));
        await page.GotoAsync("https://open.spotify.com/");
        var settings=new Settings(Path.Combine(root,"playlist-integration-settings.json"));settings.Value["service"]="spotify";settings.Save();
        using var unusedWebView=new EmbeddedMusicHost(Path.Combine(root,"unused-profile"));
        await using var controller=new BrowserController(settings,unusedWebView,host);
        var script=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"BrowserScripts.js"));
        await page.EvaluateAsync("()=>document.getElementById('library-scroll').scrollTop=20");
        var library=await controller.SpotifyLibraryAsync();
        Check(library["account"]?.GetValue<string>()=="account-a"&&library["playlists"]!.AsArray().Count==1,"Read account playlists; exclude albums and duplicate localized links");
        Check(await page.EvaluateAsync<int>("()=>document.getElementById('library-scroll').scrollTop")==20,"Restore library scroll after discovery");
        var added=await controller.SpotifyLibraryAsync("https://open.spotify.com/playlist/"+AddedId+"?si=test");
        Check(added["added"]!.GetValue<bool>()&&added["playlists"]!.AsArray().Count==2,"Import URL and merge with account mixes");
        Check((await controller.SpotifyLibraryAsync("https://open.spotify.com/playlist/"+AddedId))["added"]!.GetValue<bool>()==false,"Repeated import is idempotent");
        var privatePlaylist=await controller.SpotifyLibraryAsync("https://open.spotify.com/playlist/"+PrivateId);
        Check(privatePlaylist["playlists"]!.AsArray().OfType<JsonObject>().Any(entry=>entry["title"]!.GetValue<string>().StartsWith("Плейлист Spotify")),"Private playlist can be imported without public oEmbed metadata");
        var playing=await controller.CommandAsync("spotify","spotify_playlist","https://open.spotify.com/playlist/"+PrivateId);
        Check(playing["playing"]!.GetValue<bool>()&&playing["active_spotify_playlist"]?.GetValue<string>()==PrivateId,"Playlist click starts audio through native input and marks selection");
        Check(SpotifyPlaylists.Saved(settings,"account-a").OfType<JsonObject>().Any(entry=>entry["url"]!.GetValue<string>().EndsWith(PrivateId)&&entry["title"]!.GetValue<string>()=="Account playlist"),"Authenticated playback fills private playlist title");
        await page.EvaluateAsync("""
            async()=>{
              document.body.insertAdjacentHTML('afterbegin','<div style="display:none"><button data-testid="control-button-playpause" aria-label="Play"></button><button data-testid="control-button-skip-forward"></button><button data-testid="control-button-skip-back"></button></div>');
              // The actual Spotify engine need not be a visible media node;
              // a paused preview must not override the transport's Pause state.
              audio.remove();await new Promise(resolve=>setTimeout(resolve,50));await audio.play();
              window.__lmMedia=[];document.body.append(document.createElement('video'));
            }
            """);
        var active=await controller.SnapshotAsync("spotify");
        Check(active["playing"]!.GetValue<bool>()&&active["spotify_playlist_title"]?.GetValue<string>()=="Account playlist","Snapshot keeps playlist name and ignores paused preview media");
        foreach(var action in new[]{"next","previous","stop","play"})
        {
            JsonObject state;
            try{state=await controller.CommandAsync("spotify",action);}
            catch(Exception error){throw new InvalidOperationException("Visible Spotify transport failed: "+action+" "+await page.EvaluateAsync<string>("()=>JSON.stringify({actions:window.trustedActions,label:document.getElementById('play').getAttribute('aria-label'),title:document.getElementById('title').textContent,paused:audio.paused})"),error);}
            Check(state["spotify_playlist_title"]?.GetValue<string>()=="Account playlist"&&state["active_spotify_playlist"]?.GetValue<string>()==PrivateId,"Playlist context survives "+action);
            if(action is "stop" or "play")Check(state["playing"]!.GetValue<bool>()==(action=="play"),"Visible transport works despite hidden duplicates: "+action);
        }
        await page.EvaluateAsync("""
            ()=>{const root=document.querySelector('[data-testid="now-playing-bar"]'),r=root.getBoundingClientRect();
              const overlay=document.createElement('div');overlay.id='transport-overlay';overlay.style.cssText=`position:fixed;left:${r.left}px;top:${r.top}px;width:${r.width}px;height:${r.height}px;z-index:99999;background:#111`;document.body.append(overlay);}
            """);
        foreach(var action in new[]{"stop","play","next","previous","play_pause","play_pause"})
        {
            var state=await controller.CommandAsync("spotify",action);
            if(action is "stop" or "play")Check(state["playing"]!.GetValue<bool>()==(action=="play"),"Media Session controls obscured Spotify transport: "+action);
            Check(state["active_spotify_playlist"]?.GetValue<string>()==PrivateId,"Media commands preserve selected playlist");
        }
        Check(await page.EvaluateAsync<bool>("()=>['media:pause','media:play','media:next','media:previous'].every(action=>window.trustedActions.includes(action))"),"Fallback uses Spotify's registered OS media handlers");
        await page.EvaluateAsync("()=>document.getElementById('transport-overlay').remove()");
        await page.EvaluateAsync("()=>{document.querySelector('[data-testid=\"now-playing-bar\"]').hidden=true;window.__lmMedia=[];navigator.mediaSession.playbackState='none';}");
        await controller.CommandAsync("spotify","stop");
        Check(await page.EvaluateAsync<bool>("()=>audio.paused"),"Stop still reaches Spotify when its visual transport and playback state are unavailable");
        await page.EvaluateAsync("()=>document.querySelector('[data-testid=\"now-playing-bar\"]').hidden=false");
        var playlistPage=page.Url;
        // Playlist navigation creates a new document; verify its account once
        // before measuring repeat reads of the already identified account.
        await controller.SpotifyLibraryAsync(refresh:false);
        var cachedClock=System.Diagnostics.Stopwatch.StartNew();
        var cached=await controller.SpotifyLibraryAsync(refresh:false);
        Check(cached["playlists"]!.AsArray().Count==3&&page.Url==playlistPage,"Returning to Spotify reuses account library without navigating away from playback");
        Check(cachedClock.Elapsed<TimeSpan.FromSeconds(1),"Cached Spotify library must not repeat multi-second discovery: "+cachedClock.Elapsed.TotalMilliseconds+" ms");
        var refresh=controller.SpotifyLibraryAsync();
        await Task.Delay(500);
        controller.CancelLibraryRefresh();
        var canceled=false;
        try{await refresh.WaitAsync(TimeSpan.FromSeconds(2));}catch(OperationCanceledException){canceled=true;}
        Check(canceled,"Leaving Spotify cancels background library discovery");
        Check((await controller.SnapshotAsync("spotify"))["busy"]==null,"Canceled discovery releases the player for the next command");
        await page.EvaluateAsync("()=>{window.accountId='account-b';document.querySelector('[data-testid=\"user-widget-link\"]').textContent='Another account';document.getElementById('library-scroll').replaceChildren();}");
        var another=await controller.SpotifyLibraryAsync();
        Check(another["account"]?.GetValue<string>()=="account-b"&&another["playlists"]!.AsArray().Count==0,"Switching account hides previous user's imported playlists");
        Check((await controller.SnapshotAsync("spotify"))["active_spotify_playlist"]==null,"Switching account clears previous user's playlist context");
        Check(SpotifyPlaylists.Saved(new Settings(Path.Combine(root,"playlist-integration-settings.json")),"account-a").Count==2,"Manual playlists remain saved for original account");
    }
}
