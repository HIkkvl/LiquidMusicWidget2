using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace LiquidMusic;

// Spotify requires Widevine, which WebView2 does not provide. Use a separate
// Edge profile owned by the widget; never attach to the user's normal browser.
internal sealed class SpotifyMusicHost(string? profile=null):IAsyncDisposable
{
    private readonly string profile=profile??ProfileStorage.ProfilePath("spotify-edge");
    private readonly bool migrateProfile=profile==null;
    private IPlaywright? playwright;
    private IBrowserContext? context;
    private IPage? page;
    private int processId;
    private bool loginVisible,signedIn,autoHideAfterLogin;
    internal JsonObject Status()=>new()
    {
        ["embedded"]=true,["name"]="Spotify · фоновый Edge",["supported"]=true,["configured"]=true,
        ["connected"]=page?.IsClosed==false,["signed_in"]=signedIn,["state"]=signedIn?"connected":"sign_in",
        ["message"]=signedIn?"Spotify подключён · плеер работает в фоне":"Войдите в Spotify. Окно входа скроется после авторизации; используется отдельный профиль приложения."
    };
    internal async Task<IPage> PageAsync()
    {
        if(page?.IsClosed==false)return page;
        if(context!=null)try{await context.CloseAsync();}catch(PlaywrightException){}
        context=null;page=null;processId=0;signedIn=false;loginVisible=false;
        if(migrateProfile)ProfileStorage.Prepare("spotify-edge");
        Directory.CreateDirectory(profile);
        playwright??=await Playwright.CreateAsync();
        try
        {
            context=await playwright.Chromium.LaunchPersistentContextAsync(profile,new()
            {
                Channel="msedge",Headless=false,ViewportSize=ViewportSize.NoViewport,
                IgnoreDefaultArgs=["--disable-component-update"],
                Args=["--window-position=-32000,-32000","--window-size=1280,900","--start-minimized","--no-first-run","--deny-permission-prompts"],
                AcceptDownloads=false
            });
        }
        catch(PlaywrightException error){throw new InvalidOperationException("Не удалось запустить плеер Spotify. Проверьте, что Microsoft Edge установлен.",error);}
        await context.RouteAsync("**/*",async route=>
        {
            // Leave normal media, scripts and DRM requests untouched. Restrict
            // top-level navigation, including popups, to the service and login.
            if(route.Request.IsNavigationRequest&&route.Request.Frame.ParentFrame==null&&
                !EmbeddedMusicHost.AllowedNavigation(route.Request.Url,"spotify"))await route.AbortAsync();
            else await route.ContinueAsync();
        });
        context.Page+=(_,popup)=>
        {
            popup.Download+=async(_,download)=>await download.CancelAsync();
            popup.DOMContentLoaded+=(_,_)=>{if(!loginVisible)SetWindowsVisible(false);};
        };
        page=context.Pages.FirstOrDefault()??await context.NewPageAsync();
        page.SetDefaultTimeout(12000);
        if(context.Browser==null)throw new InvalidOperationException("Не удалось получить окно фонового плеера Spotify.");
        var session=await context.Browser.NewBrowserCDPSessionAsync();
        try
        {
            var info=await session.SendAsync("SystemInfo.getProcessInfo");
            processId=info!.Value.GetProperty("processInfo").EnumerateArray().First(p=>p.GetProperty("type").GetString()=="browser").GetProperty("id").GetInt32();
        }
        finally{await session.DetachAsync();}
        SetWindowsVisible(false);
        await page.GotoAsync(MusicServices.Origin("spotify"),new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=45000});
        return page;
    }
    internal async Task ShowLoginAsync()
    {
        var target=await PageAsync();
        autoHideAfterLogin=!signedIn;
        await target.GotoAsync(EmbeddedMusicHost.LoginUrl("spotify",MusicServices.Origin("spotify"),signedIn),
            new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=45000});
        loginVisible=true;SetWindowsVisible(true);
    }
    internal void SetSignedIn(bool value)
    {
        signedIn=value;
        if(value&&loginVisible&&autoHideAfterLogin){loginVisible=false;SetWindowsVisible(false);}
    }
    internal bool HasVisibleWindow
    {
        get
        {
            var visible=false;
            EnumWindows((window,_)=>{Native.GetWindowThreadProcessId(window,out var owner);if(owner==processId&&IsWindowVisible(window))visible=true;return true;},0);
            return visible;
        }
    }
    private void SetWindowsVisible(bool visible)
    {
        if(processId==0)return;
        EnumWindows((window,_)=>
        {
            Native.GetWindowThreadProcessId(window,out var owner);
            if(owner!=processId)return true;
            var className=new StringBuilder(256);GetClassName(window,className,className.Capacity);
            if(className.ToString()!="Chrome_WidgetWin_1")return true;
            if(visible)
            {
                var area=Screen.FromPoint(Cursor.Position).WorkingArea;
                var width=Math.Min(1040,area.Width-32);var height=Math.Min(860,area.Height-32);
                ShowWindow(window,9);
                SetWindowPos(window,0,area.Left+(area.Width-width)/2,area.Top+(area.Height-height)/2,width,height,0x0040);
                SetForegroundWindow(window);
            }
            else ShowWindow(window,0);
            return true;
        },0);
    }
    public async ValueTask DisposeAsync()
    {
        if(context!=null)try{await context.CloseAsync();}catch(PlaywrightException){}
        context=null;page=null;playwright?.Dispose();playwright=null;
    }
    private delegate bool WindowCallback(nint window,nint parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(WindowCallback callback,nint parameter);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint window,StringBuilder name,int maximum);
}
