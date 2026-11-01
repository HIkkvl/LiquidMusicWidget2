using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiquidMusic;

// A separate WebView2 environment: remote music never receives the widget's
// native bridge, host objects, API keys or the user's desktop-browser profile.
internal sealed class EmbeddedMusicHost : IDisposable
{
    private readonly string profile;
    private readonly Dictionary<string,MusicWindow> windows=new();
    private readonly Dictionary<string,bool> accounts=new();
    private readonly Dictionary<string,string> targets=new();
    private readonly List<MusicWindow> popups=new();
    private CoreWebView2Environment? environment;
    private Task? initializing;
    private bool disposed;
    public string Endpoint {get;private set;}="";
    public EmbeddedMusicHost(string? profile=null)=>this.profile=profile??Path.Combine(AppPaths.Data,"music-webview");
    internal IEnumerable<MusicWindow> Windows=>windows.Values;
    internal string? Target(string service)=>targets.GetValueOrDefault(service);
    public JsonObject Status(string service)
    {
        var ready=accounts.GetValueOrDefault(service);
        return new(){["embedded"]=true,["name"]="Встроенный плеер",["supported"]=true,
            ["configured"]=true,["connected"]=Endpoint.Length>0,["signed_in"]=ready,
            ["state"]=ready?"connected":"sign_in",["shortcuts_updated"]=false,
            ["message"]=ready?"Аккаунт подключён · музыка работает внутри виджета":"Войдите в музыкальный аккаунт. После входа окно скроется, останется только виджет."};
    }
    public Task InitializeAsync()
    {
        if(initializing?.IsFaulted==true)
        {
            foreach(var window in windows.Values)window.Shutdown();
            windows.Clear();targets.Clear();Endpoint="";initializing=null;
        }
        return initializing??=InitializeCoreAsync();
    }
    private async Task InitializeCoreAsync()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        Directory.CreateDirectory(profile);
        // Port 0 lets Chromium allocate a free loopback port. No changes to
        // Windows browser associations, shortcuts or machine-wide flags.
        environment=await CoreWebView2Environment.CreateAsync(null,profile,new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments="--remote-debugging-port=0 --remote-debugging-address=127.0.0.1"
        });
        var window=await CreateWindowAsync("yandex");
        windows["yandex"]=window;
        var files=new[]{Path.Combine(profile,"EBWebView","DevToolsActivePort"),Path.Combine(profile,"DevToolsActivePort")};
        for(var i=0;i<100;i++)
        {
            foreach(var file in files)try
            {
                var lines=await File.ReadAllLinesAsync(file);
                if(lines.Length>0&&int.TryParse(lines[0],out var port)&&port is >0 and <65536)
                {Endpoint="http://127.0.0.1:"+port;return;}
            }
            catch(IOException){}
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Не удалось запустить встроенный музыкальный плеер. Перезапустите виджет.");
    }
    private async Task<MusicWindow> CreateWindowAsync(string service)
    {
        var window=new MusicWindow(service);
        try
        {
            window.StartBackground();
            await window.View.EnsureCoreWebView2Async(environment);
            var core=window.View.CoreWebView2;
            core.Settings.IsWebMessageEnabled=false;
            core.Settings.AreHostObjectsAllowed=false;
            core.Settings.AreDevToolsEnabled=false;
            core.Settings.AreDefaultContextMenusEnabled=false;
            core.Settings.IsPasswordAutosaveEnabled=false;
            core.Settings.IsGeneralAutofillEnabled=false;
            core.NavigationStarting+=(_,e)=>{if(!AllowedNavigation(e.Uri,service))e.Cancel=true;};
            core.SourceChanged+=(_,_)=>window.SetAddress(core.Source);
            window.UserReturned+=()=>
            {
                if(window.Origin.Length>0&&(!accounts.GetValueOrDefault(service)||!BrowserController.MatchesService(core.Source,service)))core.Navigate(window.Origin);
            };
            core.PermissionRequested+=(_,e)=>
            {
                if(e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone or
                    CoreWebView2PermissionKind.Geolocation or CoreWebView2PermissionKind.Notifications or CoreWebView2PermissionKind.ClipboardRead)
                    e.State=CoreWebView2PermissionState.Deny;
            };
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
            core.NewWindowRequested+=async(_,e)=>
            {
                e.Handled=true;
                if(!window.LoginVisible||!AllowedNavigation(e.Uri,service))return;
                using var deferral=e.GetDeferral();
                try
                {
                    var popup=await CreateWindowAsync(service);
                    popups.Add(popup);e.NewWindow=popup.View.CoreWebView2;
                    popup.ShowLogin(false);
                    popup.UserReturned+=()=>
                    {
                        popup.Shutdown();popups.Remove(popup);
                        if(!accounts.GetValueOrDefault(service))core.Reload();
                    };
                    popup.View.CoreWebView2.WindowCloseRequested+=(_,_)=>
                    {
                        popup.Shutdown();popups.Remove(popup);
                        if(!accounts.GetValueOrDefault(service))core.Reload();
                    };
                }
                catch(Exception error){AppPaths.Log("Music sign-in window: "+error.GetType().Name);}
            };
            core.ProcessFailed+=(_,e)=>AppPaths.Log("Embedded music process: "+e.ProcessFailedKind);
            return window;
        }
        catch{window.Shutdown();throw;}
    }
    internal static bool AllowedNavigation(string url,string service)
    {
        if(url=="about:blank")return true;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https")return false;
        static bool Under(string host,string domain)=>host==domain||host.EndsWith("."+domain,StringComparison.OrdinalIgnoreCase);
        return service=="youtube"?Under(uri.Host,"youtube.com")||uri.Host is "accounts.google.com" or "consent.google.com":
            new[]{"yandex.ru","yandex.kz","yandex.com"}.Any(domain=>Under(uri.Host,domain));
    }
    internal async Task<MusicWindow> EnsureServiceAsync(string service,string origin)
    {
        await InitializeAsync();ObjectDisposedException.ThrowIf(disposed,this);
        if(!windows.TryGetValue(service,out var window)){window=await CreateWindowAsync(service);windows[service]=window;}
        if(window.Origin!=origin)
        {
            window.Origin=origin;
            var source=window.View.CoreWebView2.Source;
            if(!Uri.TryCreate(source,UriKind.Absolute,out var current)||current.GetLeftPart(UriPartial.Authority)!=origin.TrimEnd('/'))
            {accounts[service]=false;window.View.CoreWebView2.Navigate(origin);}
        }
        using var info=JsonDocument.Parse(await window.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Target.getTargetInfo","{}"));
        targets[service]=info.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString()!;
        return window;
    }
    internal void SetSignedIn(string service,bool value)
    {
        accounts[service]=value;
        if(value&&windows.TryGetValue(service,out var window)&&window.AutoHideAfterLogin)
        {
            window.ReturnToBackground();
            foreach(var popup in popups.ToArray()){popup.Shutdown();popups.Remove(popup);}
        }
    }
    public async Task ShowLoginAsync(string service,string origin)
    {
        var window=await EnsureServiceAsync(service,origin);
        window.ShowLogin(!accounts.GetValueOrDefault(service));
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        foreach(var window in popups.Concat(windows.Values).ToArray())window.Shutdown();
        popups.Clear();windows.Clear();
    }

    internal sealed class MusicWindow : Form
    {
        internal readonly WebView2 View=new(){Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(18,18,22)};
        private readonly Label address=new(){Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Padding=new(12,0,4,0),ForeColor=Color.White};
        private readonly Panel bar=new(){Dock=DockStyle.Top,Height=44,BackColor=Color.FromArgb(28,28,34)};
        private bool shutdown;
        internal string Origin="";
        internal bool LoginVisible {get;private set;}
        internal bool AutoHideAfterLogin {get;private set;}
        internal event Action? UserReturned;
        protected override bool ShowWithoutActivation=>!LoginVisible;
        protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x80;return p;}}
        internal MusicWindow(string service)
        {
            Text=service=="youtube"?"Вход в YouTube — Liquid Music":"Вход в Яндекс Музыку — Liquid Music";
            FormBorderStyle=FormBorderStyle.SizableToolWindow;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;
            var done=new Button{Text="Вернуться в виджет",Dock=DockStyle.Right,Width=160};
            done.Click+=(_,_)=>{ReturnToBackground();UserReturned?.Invoke();};
            bar.Controls.Add(address);bar.Controls.Add(done);Controls.Add(View);Controls.Add(bar);
            FormClosing+=(_,e)=>{if(!shutdown){e.Cancel=true;ReturnToBackground();UserReturned?.Invoke();}};
        }
        internal void SetAddress(string url)
        {
            address.Text=Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="https"?uri.Host:"Музыкальный аккаунт";
        }
        internal void StartBackground()
        {
            Location=new(-32000,-32000);ClientSize=new(1280,900);bar.Visible=false;Show();
        }
        internal void ShowLogin(bool autoHide)
        {
            AutoHideAfterLogin=autoHide;LoginVisible=true;bar.Visible=true;
            ClientSize=new(560,740);
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;
            Size=new(Math.Min(Width,area.Width),Math.Min(Height,area.Height));
            Location=new(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
            TopMost=true;Show();Activate();View.Focus();
        }
        internal void ReturnToBackground()
        {
            LoginVisible=false;AutoHideAfterLogin=false;TopMost=false;Location=new(-32000,-32000);
            bar.Visible=false;ClientSize=new(1280,900);
        }
        internal void Shutdown(){if(shutdown)return;shutdown=true;View.Dispose();Close();Dispose();}
    }
}
