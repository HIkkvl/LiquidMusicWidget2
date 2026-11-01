using System.Text.Json;
using Microsoft.Playwright;

namespace LiquidMusic;

internal static class ServicePlayerTests
{
    internal static string Fixture(string service)
    {
        var spotify=service=="spotify";
        var player=spotify?"data-testid='now-playing-bar'":"class='playControls'";
        var title=spotify?"data-testid='context-item-info-title'":"class='playbackSoundBadge__titleLink'";
        var artist=spotify?"data-testid='context-item-info-artist'":"class='playbackSoundBadge__lightLink'";
        var controls=spotify?"data-testid='control-button-playpause'":"class='playControls__play'";
        var row=spotify?"data-testid='tracklist-row'":"class='soundList__item'";
        var user=spotify?"data-testid='user-widget-link'":"class='header__userNavUsernameButton'";
        var like=spotify?"data-testid='add-button'":"class='sc-button-like'";
        var searchLabel=spotify?"Включить трек «Test Song» исполнителя Test Artist":"Play Test Song";
        var audio=Convert.ToBase64String(VoiceAssistant.ToWave(new byte[96000]));
        return $$"""
            <meta charset='utf-8'><button {{user}}>Fixture account</button>
            <div {{player}}>
             <div data-testid='now-playing-widget' class='playbackSoundBadge'>
              <a {{title}} id='title' href='/track/test'>Previous track</a><a {{artist}}>Test Artist</a>
              <button {{like}} aria-label='Like' aria-pressed='false' id='like'>Like</button>
             </div>
             <button {{controls}} aria-label='Play' id='play'>Play</button>
             <button data-testid='control-button-skip-forward' class='playControls__next' aria-label='Skip to next' id='next'>Next</button>
             <button data-testid='control-button-skip-back' class='playControls__prev' aria-label='Skip to previous' id='previous'>Previous</button>
             <div data-testid='volume-bar'><input type='range' aria-label='Volume' min='0' max='1' step='.01' value='1'></div>
             <span>0:00</span><span>3:00</span>
            </div>
            <div {{row}}>Other Artist Other Song<button aria-label='Play Other Song'>Play</button></div>
            <div {{row}} id='result'>Test Artist Test Song<button aria-label='{{searchLabel}}' id='search'>Play</button></div>
            <audio id='audio' loop src='data:audio/wav;base64,{{audio}}'></audio>
            <script>
            const audio=document.getElementById('audio');window.trustedActions=[];
            document.addEventListener('click',async event=>{
              if(!event.isTrusted)return;
              const id=event.target.id;window.trustedActions.push(id);
              if(id==='search'||id==='play'){
                if(id==='search')document.getElementById('title').textContent='Test Song';
                if(audio.paused){await audio.play();document.getElementById('play').setAttribute('aria-label','Pause');}
                else{audio.pause();document.getElementById('play').setAttribute('aria-label','Play');}
              }
              if(id==='next')document.getElementById('title').textContent='Next Song';
              if(id==='previous')document.getElementById('title').textContent='Test Song';
              if(id==='like')event.target.setAttribute('aria-pressed',String(event.target.getAttribute('aria-pressed')!=='true'));
            });
            document.querySelector('input').addEventListener('input',event=>audio.volume=Number(event.target.value));
            </script>
            """;
    }
    internal static async Task CheckAdapterAsync(IPage page,string service)
    {
        void Check(bool result,string message){if(!result)throw new InvalidOperationException(service+": "+message);}
        var script=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"BrowserScripts.js"));
        var focus=await page.Context.NewCDPSessionAsync(page);
        await focus.SendAsync("Emulation.setFocusEmulationEnabled",new Dictionary<string,object>{{"enabled",true}});
        try
        {
            await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=Fixture(service)}));
            await BrowserController.SearchServiceAsync(page,service,"Test Artist Test Song",script);
            var state=await page.EvaluateAsync<JsonElement>(script,new{action="snapshot",service});
            Check(state.GetProperty("playing").GetBoolean()&&state.GetProperty("signed_in").GetBoolean()&&state.GetProperty("title").GetString()=="Test Song","Trusted search starts matching song");
            Check(state.GetProperty("artist").GetString()=="Test Artist","Correct artist metadata");
            await page.WaitForFunctionAsync("()=>document.querySelector('audio').currentTime>0",null,new(){Timeout=5000});
            await page.EvaluateAsync<JsonElement>(script,new{action="volume",service,volume=.4});
            Check(await page.EvaluateAsync<bool>("()=>Math.abs(document.querySelector('audio').volume-.4)<.01"),"Player volume changes");
            foreach(var command in new[]{"pause","play","next","previous","like"})
            {
                await page.EvaluateAsync<JsonElement>(script,new{action="target",service,command});
                Check(await BrowserController.ClickControlAsync(page,"target"),"Trusted "+command);
                state=await page.EvaluateAsync<JsonElement>(script,new{action="snapshot",service});
                if(command is "pause" or "play")Check(state.GetProperty("playing").GetBoolean()==(command=="play"),"Playback state after "+command);
                if(command=="next")Check(state.GetProperty("title").GetString()=="Next Song","Skip updates track");
                if(command=="like")Check(state.GetProperty("liked").GetBoolean(),"Like state updates");
            }
            Check(!await page.EvaluateAsync<bool>(script,new{action="search_target",service,query="Wrong Artist Test Song"}),"Never plays mismatched artist");
        }
        finally{await focus.SendAsync("Emulation.setFocusEmulationEnabled",new Dictionary<string,object>{{"enabled",false}});await focus.DetachAsync();}
    }
    internal static async Task CheckHostsAsync()
    {
        var root=Path.Combine(AppPaths.Data,"service-tests",Guid.NewGuid().ToString("N"));
        var checks=new List<string>();
        foreach(var service in new[]{"spotify","soundcloud"})
        {
            if(!BrowserController.MatchesService(MusicServices.Origin(service),service)||
                BrowserController.MatchesService("https://"+new Uri(MusicServices.Origin(service)).Host+".evil.example/",service)||
                EmbeddedMusicHost.AllowedNavigation("https://evil.example/",service))throw new InvalidOperationException("Service navigation boundaries: "+service);
        }
        var legacyInstall=Path.Combine(root,"legacy-build");
        await using(var spotify=new SpotifyMusicHost(Path.Combine(legacyInstall,".native-data","spotify-edge")))
        {
            var page=await spotify.PageAsync();
            if(spotify.HasVisibleWindow)throw new InvalidOperationException("Spotify startup must not leave a visible browser window.");
            var drm=await page.EvaluateAsync<JsonElement>("""
                async()=>{
                  const result={};
                  for(const key of ['com.widevine.alpha','com.microsoft.playready.recommendation']){
                    try{await navigator.requestMediaKeySystemAccess(key,[{initDataTypes:['cenc'],audioCapabilities:[{contentType:'audio/mp4; codecs="mp4a.40.2"'}]},{initDataTypes:['cenc'],videoCapabilities:[{contentType:'video/mp4; codecs="avc1.42E01E"'}]}]);result[key]='supported';}catch(error){result[key]=error.name+': '+error.message;}
                  }
                  return result;
                }
                """);
            File.WriteAllText(Path.Combine(AppPaths.Logs,"spotify-drm-test.json"),drm.GetRawText());
            if(!drm.EnumerateObject().Any(property=>property.Value.GetString()=="supported"))throw new InvalidOperationException("Installed Edge does not offer protected playback for Spotify: "+drm.GetRawText());
            await CheckAdapterAsync(page,"spotify");
            if(await page.EvaluateAsync<bool>("()=>typeof window.liquid!=='undefined'"))throw new InvalidOperationException("Native bridge leaked into Spotify");
            await page.EvaluateAsync("()=>{document.cookie='serviceSessionProbe=ok;Path=/;Max-Age=3600;Secure;SameSite=Lax';}");
            await page.ReloadAsync();
            if(!await page.EvaluateAsync<bool>("()=>document.cookie.includes('serviceSessionProbe=ok')"))throw new InvalidOperationException("Spotify session cookie not retained");
            checks.Add("Spotify: private background Edge, Widevine, metadata, trusted search/play/pause/skip/like, volume and isolated session");
        }
        var migratedSpotify=ProfileStorage.Prepare("spotify-edge",Path.Combine(root,"shared-profiles"),new[]{legacyInstall});
        await using(var restoredSpotify=new SpotifyMusicHost(migratedSpotify))
        {
            var restoredPage=await restoredSpotify.PageAsync();
            if(!await restoredPage.EvaluateAsync<bool>("()=>document.cookie.includes('serviceSessionProbe=ok')"))throw new InvalidOperationException("Spotify session cookie did not survive profile migration and restart.");
            checks.Add("Spotify encrypted session cookie survives migration from old build and engine restart");
        }
        using(var host=new EmbeddedMusicHost(Path.Combine(root,"soundcloud")))
        {
            await host.InitializeAsync();
            using var playwright=await Playwright.CreateAsync();
            var transport=await playwright.Chromium.ConnectOverCDPAsync(host.Endpoint,new(){NoDefaults=true});
            var context=transport.Contexts.Single();
            await context.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=Fixture("soundcloud")}));
            var settings=new Settings(Path.Combine(root,"settings.json"));settings.Value["service"]="soundcloud";settings.Save();
            await using var controller=new BrowserController(settings,host);
            var state=await controller.CommandAsync("soundcloud","search_play","Test Artist Test Song");
            if(state["playing"]?.GetValue<bool>()!=true||state["title"]?.GetValue<string>()!="Test Song")throw new InvalidOperationException("SoundCloud embedded search did not play");
            state=await controller.CommandAsync("soundcloud","pause");
            if(state["playing"]?.GetValue<bool>()==true)throw new InvalidOperationException("SoundCloud pause did not reach WebView2");
            var page=context.Pages.Last();await CheckAdapterAsync(page,"soundcloud");
            if(host.Windows.Any(window=>window.LoginVisible||window.ShowInTaskbar))throw new InvalidOperationException("SoundCloud host must stay hidden");
            checks.Add("SoundCloud: hidden WebView2, full controller search and pause, metadata, trusted transport, likes and volume");
        }
        File.WriteAllText(Path.Combine(AppPaths.Logs,"services-selftest.json"),JsonSerializer.Serialize(new{ok=true,checks}));
    }
}
