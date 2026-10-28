using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace LiquidMusic;

internal sealed class BrowserController : IAsyncDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private IPlaywright? playwright;
    private IBrowser? browser;
    private readonly Dictionary<string,IPage> selected=new();
    private readonly string script=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"BrowserScripts.js"));
    private readonly YandexMusicRegion yandex;
    public BrowserController(Settings? settings=null)=>yandex=new(settings??new Settings());
    public void SetYandexDomain(string domain)
    {
        yandex.Select(domain);selected.Remove("yandex");
    }
    private bool AcceptsPage(string url,string service)=>MatchesService(url,service)&&(service!="yandex"||yandex.Accepts(url));
    private IPage RememberPage(IPage page,string service)
    {
        if(service=="yandex")yandex.Remember(page.Url);
        selected[service]=page;return page;
    }

    public static bool MatchesService(string url,string service)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https")return false;
        return service=="youtube" ? uri.Host is "www.youtube.com" or "youtube.com" or "music.youtube.com" : uri.Host is "music.yandex.ru" or "music.yandex.kz" or "music.yandex.com";
    }
    private string attachedExecutable="";
    public JsonObject Status()
    {
        var info=BrowserSetup.DetectDefault();
        var saved=info.Supported?BrowserSetup.Read(info):null;
        var connected=info.Supported && browser?.IsConnected==true && attachedExecutable.Length>0 && BrowserSetup.SameExecutable(attachedExecutable,info.Executable);
        var state=!info.Supported?"unsupported":BrowserSetup.Configuring?"connecting":connected?"connected":saved!=null?"saved":"not_configured";
        var message=state switch
        {
            "unsupported"=>info.Error,"connecting"=>"Подключение…","connected"=>saved?.UserDataDir!=null?"Подключено — отдельный профиль. При первом запуске войдите в музыкальный аккаунт.":"Подключено",
            "saved"=>"Настройка сохранена — подключение восстановится автоматически",
            _=>"Подключите браузер один раз"
        };
        return new(){["name"]=info.Name,["supported"]=info.Supported,["configured"]=saved!=null,["connected"]=connected,["state"]=state,["message"]=message,["shortcuts_updated"]=saved?.ShortcutsUpdated==true};
    }
    public async Task InitializeConnectionAsync()
    {
        var info=BrowserSetup.DetectDefault();
        if(!info.Supported||BrowserSetup.Read(info)==null)return;
        await gate.WaitAsync();
        try{await ConnectAsync(true);}catch(Exception error){AppPaths.Log(error.Message);}finally{gate.Release();}
    }
    public async Task ConfigureAsync(bool updateShortcuts)
    {
        await gate.WaitAsync();
        try{await BrowserSetup.ConfigureAsync(true,updateShortcuts);await ConnectAsync(false);}finally{gate.Release();}
    }
    private async Task ConnectAsync(bool launch)
    {
        var info=BrowserSetup.DetectDefault();
        if(!info.Supported)throw new InvalidOperationException(info.Error);
        if(browser?.IsConnected==true&&attachedExecutable.Length>0&&BrowserSetup.SameExecutable(attachedExecutable,info.Executable))return;
        if(attachedExecutable.Length>0&&!BrowserSetup.SameExecutable(attachedExecutable,info.Executable))
        {
            playwright?.Dispose();playwright=null;browser=null;selected.Clear();
        }
        var saved=BrowserSetup.Read(info);
        if(saved==null)throw new InvalidOperationException("Нужно один раз подключить "+info.Name+": настройки → «Подключить браузер».");
        if(launch&&(saved.UserDataDir!=null||!BrowserSetup.IsRunning(info)))await BrowserSetup.LaunchAsync(info,saved,true);
        playwright ??= await Playwright.CreateAsync();
        try
        {
            browser=await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{saved.Port}",new(){Timeout=4000,NoDefaults=true});
            attachedExecutable=info.Executable;selected.Clear();
        }
        catch(Exception error){throw new InvalidOperationException("Подключение к "+info.Name+" временно недоступно. Настройка сохранена. Запускайте браузер через настроенный ярлык или приложение.",error);}
    }
    private async Task<IPage> PageAsync(string service,bool create)
    {
        await ConnectAsync(create);
        if(selected.TryGetValue(service,out var previous)&&!previous.IsClosed&&MatchesService(previous.Url,service))return RememberPage(previous,service);
        var candidates=browser!.Contexts.SelectMany(context=>context.Pages).Where(page=>AcceptsPage(page.Url,service)).ToList();
        // Reuse a playing music tab before considering a new tab. Never inspect
        // unrelated pages, profile cookies or account storage.
        foreach(var candidate in candidates)
        {
            try {if(await candidate.EvaluateAsync<bool>("() => [...document.querySelectorAll('audio,video')].some(m=>!m.paused)"))return RememberPage(candidate,service);}catch{ }
        }
        var page=candidates.FirstOrDefault();
        if(page==null)
        {
            if(!create)throw new InvalidOperationException("Музыкальная вкладка будет открыта при первой команде.");
            var url=service=="youtube"?"https://www.youtube.com/":yandex.Origin;
            var existingPages=browser.Contexts.SelectMany(context=>context.Pages).ToHashSet();
            var session=await browser.NewBrowserCDPSessionAsync();
            try{await session.SendAsync("Target.createTarget",new Dictionary<string,object>{{"url",url},{"background",true}});}finally{await session.DetachAsync();}
            for(var i=0;i<60;i++)
            {
                // A site may redirect between its regional domains. Accept the
                // newly opened tab after a redirect, without reusing an older
                // tab belonging to a different explicit region.
                page=browser.Contexts.SelectMany(c=>c.Pages).FirstOrDefault(p=>!existingPages.Contains(p)&&MatchesService(p.Url,service));
                if(page!=null)break;
                await Task.Delay(100);
            }
            if(page==null)throw new InvalidOperationException("Браузер не открыл музыкальную вкладку.");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,new(){Timeout=20000});
        }
        page.SetDefaultTimeout(12000);
        return RememberPage(page,service);
    }
    private async Task<JsonObject> ReadAsync(IPage page,string service)
    {
        if(!MatchesService(page.Url,service))throw new InvalidOperationException("Музыкальная вкладка перешла на другой сайт. Завершите вход в браузере и повторите.");
        if(service=="yandex")yandex.Remember(page.Url);
        var result=await page.EvaluateAsync<JsonElement>(script,new{action="snapshot",service});
        return JsonNode.Parse(result.GetRawText())?.AsObject()??new();
    }
    public async Task<JsonObject> SnapshotAsync(string service)
    {
        if(!await gate.WaitAsync(0))return new(){["busy"]=true};
        try{return await ReadAsync(await PageAsync(service,false),service);}
        catch(Exception e){return new(){["connected"]=false,["service"]=service,["error"]=e.Message};}
        finally{gate.Release();}
    }
    public async Task<object> DiagnoseAsync(string service)
    {
        await gate.WaitAsync();
        try
        {
            var page=await PageAsync(service,false);
            var state=await ReadAsync(page,service);
            var detail=await page.EvaluateAsync<JsonElement>("""
            () => ({url:location.origin+location.pathname,activation:{active:navigator.userActivation?.isActive,ever:navigator.userActivation?.hasBeenActive},media:[...document.querySelectorAll('audio,video')].map(el=>({paused:el.paused,muted:el.muted,ready:el.readyState,position:el.currentTime,duration:Number.isFinite(el.duration)?el.duration:0})),playerText:[...document.querySelectorAll('[class*="PlayerBar"]')].slice(0,3).map(el=>el.innerText?.slice(0,500)),buttons:[...document.querySelectorAll('[class*="PlayerBar"] button')].map(el=>({label:el.getAttribute('aria-label'),cls:el.className,disabled:el.disabled})).slice(0,20),dialogs:[...document.querySelectorAll('[role="dialog"]')].map(el=>el.innerText?.slice(0,400))})
            """);
            return new{state,detail};
        }
        finally{gate.Release();}
    }
    public async Task<JsonObject> CommandAsync(string service,string action,string? query=null,double? volume=null)
    {
        await gate.WaitAsync();
        ICDPSession? focus=null;
        try
        {
            var page=await PageAsync(service,true);
            if(action=="open")return await ReadAsync(page,service);
            // NoDefaults preserves the user's ordinary browser state, including
            // disabled focus emulation. Temporarily focus just this music target
            // inside Chromium so background playback accepts trusted input.
            focus=await page.Context.NewCDPSessionAsync(page);
            await focus.SendAsync("Emulation.setFocusEmulationEnabled",new Dictionary<string,object>{{"enabled",true}});
            if(action=="genre")
            {
                var url=service=="youtube"?"https://www.youtube.com/results?search_query=":new Uri(page.Url).GetLeftPart(UriPartial.Authority)+"/search?text=";
                await page.GotoAsync(url+Uri.EscapeDataString(query+" плейлист"),new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=25000});
                return await ReadAsync(page,service);
            }
            if(action=="search_play")
            {
                if(string.IsNullOrWhiteSpace(query)||query.Length>200)throw new ArgumentException("Укажите название трека");
                if(service=="youtube")await SearchYouTubeAsync(page,query);
                else await SearchYandexAsync(page,query,script);
            }
            else if(action=="volume")
            {
                var result=await page.EvaluateAsync<JsonElement>(script,new{action,service,volume=Math.Clamp(volume??1,0,1)});
                if(result.TryGetProperty("error",out var error))throw new InvalidOperationException(error.GetString());
            }
            else
            {
                if(action is not("next" or "previous" or "pause" or "stop" or "play" or "play_pause" or "like"))throw new ArgumentException("Неизвестная команда");
                var result=await page.EvaluateAsync<JsonElement>(script,new{action="target",command=action,service});
                if(result.TryGetProperty("already",out var already)&&already.GetBoolean())return await ReadAsync(page,service);
                if(!result.TryGetProperty("found",out var found)||!found.GetBoolean())throw new InvalidOperationException("На сайте не найдена кнопка «"+action+"». Проверьте вход в аккаунт и доступность плеера.");
                if(!await ClickControlAsync(page,"target"))throw new InvalidOperationException("Кнопка плеера перекрыта. Закройте диалог в музыкальной вкладке и повторите.");
            }
            // A successful click is not a playback confirmation.
            var resumed=false;
            for(var i=0;i<40;i++)
            {
                await Task.Delay(250);
                var snapshot=await ReadAsync(page,service);
                var playing=snapshot["playing"]?.GetValue<bool>()==true;
                if(action=="search_play"&&playing&&VoiceMatch(snapshot,query!))return snapshot;
                if(action=="search_play"&&service=="yandex"&&!playing&&!resumed&&i>=2&&VoiceMatch(snapshot,query!))
                {
                    resumed=await ResumeYandexAsync(page,script);
                }
                if(action is "pause" or "stop" && !playing)return snapshot;
                if(action=="play"&&playing)return snapshot;
                if(action is not("search_play" or "pause" or "stop" or "play"))return snapshot;
            }
            throw new InvalidOperationException("Кнопка нажата, но сайт не подтвердил воспроизведение. Проверьте вход, подписку и ограничения автозапуска в браузере.");
        }
        finally
        {
            try
            {
                if(focus!=null)
                {
                    try{await focus.SendAsync("Emulation.setFocusEmulationEnabled",new Dictionary<string,object>{{"enabled",false}});}
                    catch(PlaywrightException){}
                    try{await focus.DetachAsync();}catch(PlaywrightException){}
                }
            }
            finally{gate.Release();}
        }
    }
    internal static bool VoiceMatch(JsonObject snapshot,string query)
    {
        static string Normalize(string value)=>MusicNames.Normalize(value);
        var text=" "+Normalize((snapshot["title"]?.GetValue<string>()??"")+" "+(snapshot["artist"]?.GetValue<string>()??""))+" ";
        var words=Normalize(query).Split(' ',StringSplitOptions.RemoveEmptyEntries);
        return words.Length>0&&words.All(word=>text.Contains(" "+word+" "));
    }
    internal static async Task SearchYandexAsync(IPage page,string query,string script)
    {
        var origin=new Uri(page.Url).GetLeftPart(UriPartial.Authority);
        await page.GotoAsync(origin+"/search?text="+Uri.EscapeDataString(query),new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=25000});
        var tabs=page.GetByRole(AriaRole.Tab,new(){NameRegex=new Regex("^(Треки|Tracks|Тректер|Әндер)$",RegexOptions.IgnoreCase)});
        try
        {
            await tabs.First.WaitForAsync(new(){State=WaitForSelectorState.Attached,Timeout=10000});
            if(await tabs.First.GetAttributeAsync("aria-selected")!="true")
            {
                await tabs.First.EvaluateAsync("el=>el.setAttribute('data-liquid-control','tracks-tab')");
                await ClickControlAsync(page,"tracks-tab");
            }
        }
        catch(TimeoutException){AppPaths.Log("Search tracks tab absent; trying track-only results.");}
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(DateTime.UtcNow<deadline)
        {
            var target=await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query});
            if(target)
            {
                try
                {
                    // Real browser input triggers pointer handlers and reveals
                    // CSS hover controls. Hit testing prevents clicking
                    // an overlaid track link instead of the play button.
                    var clicked=await ClickControlAsync(page,"search","search-card");
                    if(clicked)
                    {
                        // Streamed React cards can be visible before their
                        // handlers are hydrated. An input event alone is not
                        // evidence that the site selected the requested track.
                        for(var attempt=0;attempt<8;attempt++)
                        {
                            await Task.Delay(250);
                            var state=await page.EvaluateAsync<JsonElement>(script,new{action="snapshot",service="yandex"});
                            var snapshot=JsonNode.Parse(state.GetRawText())!.AsObject();
                            if(VoiceMatch(snapshot,query))return;
                        }
                    }
                }
                catch(Exception error) when(error is PlaywrightException or TimeoutException){/* React may replace the card: locate it again. */}
            }
            await Task.Delay(350);
        }
        var matched=await page.EvaluateAsync<bool>(script,new{action="search_match",service="yandex",query});
        throw new InvalidOperationException(matched?"Найден трек «"+query+"», но его кнопка запуска недоступна или сайт не обработал нажатие. Закройте диалог в музыкальной вкладке и повторите.":"В результатах поиска не найдено точное совпадение названия и исполнителя «"+query+"». Уточните запрос.");
    }
    internal static async Task<bool> ResumeYandexAsync(IPage page,string script)
    {
        var target=await page.EvaluateAsync<JsonElement>(script,new{action="target",command="play",service="yandex"});
        if(!target.TryGetProperty("found",out var found)||!found.GetBoolean())return false;
        try{return await ClickControlAsync(page,"target");}
        catch(Exception error) when(error is PlaywrightException or TimeoutException){return false;}
    }
    internal static async Task<bool> ClickControlAsync(IPage page,string marker,string? hoverMarker=null)
    {
        // Locator.Click/Hover wait for animation frames to establish stability.
        // Background/minimized tabs can suspend those frames. Use trusted CDP
        // pointer input after synchronous geometry and hit testing instead.
        const string pointScript="""
            ({marker,check})=>{
              const el=document.querySelector('[data-liquid-control="'+CSS.escape(marker)+'"]');
              if(!el)return null;
              if(!check)el.scrollIntoView({block:'nearest',inline:'nearest',behavior:'instant'});
              const rect=el.getBoundingClientRect();
              const left=Math.max(0,rect.left),right=Math.min(innerWidth,rect.right),top=Math.max(0,rect.top),bottom=Math.min(innerHeight,rect.bottom);
              if(right<=left||bottom<=top)return null;
              const x=(left+right)/2,y=(top+bottom)/2;
              if(check){
                const style=getComputedStyle(el),hit=document.elementFromPoint(x,y);
                if(style.visibility==='hidden'||style.display==='none'||el.disabled||el.getAttribute('aria-disabled')==='true'||!hit||!(hit===el||el.contains(hit)))return null;
              }
              return {x,y};
            }
            """;
        var hover=await page.EvaluateAsync<JsonElement>(pointScript,new{marker=hoverMarker??marker,check=false});
        if(hover.ValueKind!=JsonValueKind.Object)return false;
        await page.Mouse.MoveAsync(hover.GetProperty("x").GetSingle(),hover.GetProperty("y").GetSingle());
        if(hoverMarker!=null)await Task.Delay(100);
        var point=await page.EvaluateAsync<JsonElement>(pointScript,new{marker,check=true});
        if(point.ValueKind!=JsonValueKind.Object)return false;
        var x=point.GetProperty("x").GetSingle();var y=point.GetProperty("y").GetSingle();
        // Hovering the exact button can reveal an overlay; check once more
        // before clicking, so an overlaid track link cannot receive the click.
        await page.Mouse.MoveAsync(x,y);
        point=await page.EvaluateAsync<JsonElement>(pointScript,new{marker,check=true});
        if(point.ValueKind!=JsonValueKind.Object)return false;
        await page.EvaluateAsync("""
            marker=>{
              window.__liquidInputReceived=false;
              window.__liquidInputHandler=event=>{
                const el=document.querySelector('[data-liquid-control="'+CSS.escape(marker)+'"]');
                window.__liquidInputReceived=!!(event.isTrusted&&el&&(event.target===el||el.contains(event.target)));
              };
              window.addEventListener('click',window.__liquidInputHandler,{capture:true,once:true});
            }
            """,marker);
        await page.Mouse.ClickAsync(point.GetProperty("x").GetSingle(),point.GetProperty("y").GetSingle());
        return await page.EvaluateAsync<bool>("""
            ()=>{
              window.removeEventListener('click',window.__liquidInputHandler,true);
              const received=window.__liquidInputReceived;
              delete window.__liquidInputHandler;delete window.__liquidInputReceived;
              return received===true;
            }
            """);
    }
    private async Task SearchYouTubeAsync(IPage page,string query)
    {
        var music=new Uri(page.Url).Host=="music.youtube.com";
        await page.GotoAsync(music?"https://music.youtube.com/search?q="+Uri.EscapeDataString(query):"https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query),new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=25000});
        var link=music?page.Locator("ytmusic-responsive-list-item a[href*='watch?v=']").First:page.Locator("ytd-video-renderer a#video-title[href*='watch?v=']").First;
        await link.WaitForAsync(new(){State=WaitForSelectorState.Attached,Timeout=15000});
        await link.EvaluateAsync("el=>el.click()");
        var media=page.Locator("video,audio").First;
        await media.WaitForAsync(new(){State=WaitForSelectorState.Attached,Timeout=20000});
        await media.EvaluateAsync("async el => { if(el.paused) await el.play(); }");
    }
    public ValueTask DisposeAsync()
    {
        // Disposing the transport disconnects from daily Brave. Never Browser.close.
        playwright?.Dispose();
        return ValueTask.CompletedTask;
    }
}
