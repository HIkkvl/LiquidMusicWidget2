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
        if(selected.TryGetValue(service,out var previous)&&!previous.IsClosed&&MatchesService(previous.Url,service))return previous;
        var candidates=browser!.Contexts.SelectMany(context=>context.Pages).Where(page=>MatchesService(page.Url,service)).ToList();
        // Reuse a playing music tab before considering a new tab. Never inspect
        // unrelated pages, profile cookies or account storage.
        foreach(var candidate in candidates)
        {
            try {if(await candidate.EvaluateAsync<bool>("() => [...document.querySelectorAll('audio,video')].some(m=>!m.paused)")){selected[service]=candidate;return candidate;}}catch{ }
        }
        var page=candidates.FirstOrDefault();
        if(page==null)
        {
            if(!create)throw new InvalidOperationException("Музыкальная вкладка будет открыта при первой команде.");
            var url=service=="youtube"?"https://www.youtube.com/":"https://music.yandex.kz/";
            var session=await browser.NewBrowserCDPSessionAsync();
            try{await session.SendAsync("Target.createTarget",new Dictionary<string,object>{{"url",url},{"background",true}});}finally{await session.DetachAsync();}
            for(var i=0;i<60;i++)
            {
                page=browser.Contexts.SelectMany(c=>c.Pages).FirstOrDefault(p=>MatchesService(p.Url,service));
                if(page!=null)break;
                await Task.Delay(100);
            }
            if(page==null)throw new InvalidOperationException("Браузер не открыл музыкальную вкладку.");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,new(){Timeout=20000});
        }
        page.SetDefaultTimeout(12000);
        selected[service]=page;
        return page;
    }
    private async Task<JsonObject> ReadAsync(IPage page,string service)
    {
        if(!MatchesService(page.Url,service))throw new InvalidOperationException("Музыкальная вкладка перешла на другой сайт. Завершите вход в браузере и повторите.");
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
        try
        {
            var page=await PageAsync(service,true);
            if(action=="open")return await ReadAsync(page,service);
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
                else await SearchYandexAsync(page,query);
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
                await page.Locator("[data-liquid-control='target']").EvaluateAsync("el=>el.click()");
            }
            // A successful click is not a playback confirmation.
            for(var i=0;i<40;i++)
            {
                await Task.Delay(250);
                var snapshot=await ReadAsync(page,service);
                var playing=snapshot["playing"]?.GetValue<bool>()==true;
                if(action=="search_play"&&playing&&VoiceMatch(snapshot,query!))return snapshot;
                if(action is "pause" or "stop" && !playing)return snapshot;
                if(action=="play"&&playing)return snapshot;
                if(action is not("search_play" or "pause" or "stop" or "play"))return snapshot;
            }
            throw new InvalidOperationException("Кнопка нажата, но сайт не подтвердил воспроизведение. Проверьте вход, подписку и ограничения автозапуска в браузере.");
        }
        finally{gate.Release();}
    }
    internal static bool VoiceMatch(JsonObject snapshot,string query)
    {
        static string Normalize(string value)=>Regex.Replace(value.ToLowerInvariant().Replace('ё','е'),@"[^\p{L}\p{N}]+"," ").Trim();
        var text=" "+Normalize((snapshot["title"]?.GetValue<string>()??"")+" "+(snapshot["artist"]?.GetValue<string>()??""))+" ";
        return Normalize(query).Split(' ',StringSplitOptions.RemoveEmptyEntries).All(word=>text.Contains(" "+word+" "));
    }
    private async Task SearchYandexAsync(IPage page,string query)
    {
        var origin=new Uri(page.Url).GetLeftPart(UriPartial.Authority);
        await page.GotoAsync(origin+"/search?text="+Uri.EscapeDataString(query),new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=25000});
        var tabs=page.GetByRole(AriaRole.Tab,new(){NameRegex=new Regex("^(Треки|Tracks|Тректер|Әндер)$",RegexOptions.IgnoreCase)});
        try
        {
            await tabs.First.WaitForAsync(new(){State=WaitForSelectorState.Attached,Timeout=10000});
            await tabs.First.EvaluateAsync("el=>{if(el.getAttribute('aria-selected')!=='true')el.click();}");
        }
        catch(TimeoutException){AppPaths.Log("Search tracks tab absent; trying track-only results.");}
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(DateTime.UtcNow<deadline)
        {
            var target=await page.EvaluateAsync<bool>(script,new{action="search_click",service="yandex",query});
            if(target)
            {
                // Find and invoke in a single browser task. React cannot replace
                // the node between those steps, and hover overlays cannot steal
                // a coordinate click. CDP execution never activates the OS window.
                return;
            }
            await Task.Delay(350);
        }
        throw new InvalidOperationException("Не найдена доступная кнопка запуска трека «"+query+"». Проверьте результаты поиска в Brave.");
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
