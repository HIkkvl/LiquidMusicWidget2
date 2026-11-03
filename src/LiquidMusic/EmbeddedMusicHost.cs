using System.Text.Json;
using System.Text.Json.Nodes;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiquidMusic;

// A separate WebView2 environment: remote music never receives the widget's
// native bridge, host objects, API keys or the user's desktop-browser profile.
internal sealed class EmbeddedMusicHost : IDisposable
{
    private readonly string profile;
    private readonly bool migrateProfile;
    private readonly Func<string> design;
    private readonly Dictionary<string,MusicWindow> windows=new();
    private readonly Dictionary<string,bool> accounts=new();
    private readonly Dictionary<string,string> targets=new();
    private readonly List<MusicWindow> popups=new();
    private CoreWebView2Environment? environment;
    private Task? initializing;
    private bool disposed;
    public string Endpoint {get;private set;}="";
    public EmbeddedMusicHost(string? profile=null,Func<string>? design=null)
    {this.profile=profile??ProfileStorage.ProfilePath("music-webview");migrateProfile=profile==null;this.design=design??(()=>"aurora");}
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
        if(migrateProfile)ProfileStorage.Prepare("music-webview");
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
        var window=new MusicWindow(service,design);
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
        return service switch
        {
            "youtube"=>Under(uri.Host,"youtube.com")||uri.Host is "accounts.google.com" or "consent.google.com",
            "spotify"=>Under(uri.Host,"spotify.com")||uri.Host is "accounts.google.com" or "appleid.apple.com",
            "soundcloud"=>Under(uri.Host,"soundcloud.com")||uri.Host is "accounts.google.com" or "appleid.apple.com" or "www.facebook.com",
            "yandex"=>new[]{"yandex.ru","yandex.kz","yandex.com"}.Any(domain=>Under(uri.Host,domain)),
            _=>false
        };
    }
    internal async Task<MusicWindow> EnsureServiceAsync(string service,string origin)
    {
        await InitializeAsync();ObjectDisposedException.ThrowIf(disposed,this);
        if(!windows.TryGetValue(service,out var window)){window=await CreateWindowAsync(service);windows[service]=window;}
        window.View.CoreWebView2.Resume();window.View.Visible=true;
        if(window.Origin!=origin)
        {
            window.Origin=origin;
            var source=window.View.CoreWebView2.Source;
            if(!Uri.TryCreate(source,UriKind.Absolute,out var current)||current.GetLeftPart(UriPartial.Authority)!=origin.TrimEnd('/'))
            {accounts[service]=false;window.View.CoreWebView2.Navigate(origin);}
        }
        if(!targets.ContainsKey(service))
        {
            using var info=JsonDocument.Parse(await window.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Target.getTargetInfo","{}"));
            targets[service]=info.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString()!;
        }
        return window;
    }
    internal async Task SuspendInactiveAsync(string service)
    {
        foreach(var (name,window) in windows)
        {
            if(name==service||window.LoginVisible)continue;
            var core=window.View.CoreWebView2;
            // Never freeze an engine which still has audio (e.g. pause failed).
            if(core.IsDocumentPlayingAudio||core.IsSuspended)continue;
            window.View.Visible=false;
            try{await core.TrySuspendAsync();}catch(InvalidOperationException){}
        }
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
        window.View.CoreWebView2.Navigate(LoginUrl(service,origin,accounts.GetValueOrDefault(service)));
        window.ShowLogin(service!="soundcloud"||!accounts.GetValueOrDefault(service));
    }
    internal static string LoginUrl(string service,string origin,bool signedIn=false)
    {
        if(!AllowedNavigation(origin,service)||!BrowserController.MatchesService(origin,service))
            throw new ArgumentException("Ожидался адрес музыкального сервиса.",nameof(origin));
        var destination=Uri.EscapeDataString(origin);
        if(service=="spotify")return "https://accounts.spotify.com/login?continue="+destination;
        if(service=="soundcloud")return "https://soundcloud.com/signin?redirect_url=%2Fdiscover";
        return service=="youtube"?"https://accounts.google.com/ServiceLogin?continue="+destination:
            "https://passport."+new Uri(origin).Host["music.".Length..]+"/auth"+(signedIn?"/add":"")+"?retpath="+destination;
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
        private readonly LoginHeader bar;
        private readonly LoginFooter footer=new(){Dock=DockStyle.Bottom,Height=38};
        private readonly LoginViewFrame frame=new(){Dock=DockStyle.Fill};
        private readonly Func<string> design;
        private LoginTheme theme=LoginTheme.For("aurora");
        private Bitmap? backdrop;
        private bool shutdown;
        internal string Origin="";
        internal bool LoginVisible {get;private set;}
        internal bool AutoHideAfterLogin {get;private set;}
        internal event Action? UserReturned;
        protected override bool ShowWithoutActivation=>!LoginVisible;
        protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x80;return p;}}
        internal MusicWindow(string service,Func<string> design)
        {
            this.design=design;
            Text="Вход · "+MusicServices.Name(service)+" — Liquid Music";
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;
            Font=new Font("Segoe UI",10);DoubleBuffered=true;
            bar=new LoginHeader(MusicServices.Name(service)){Dock=DockStyle.Top,Height=88};
            bar.Done+=()=>{ReturnToBackground();UserReturned?.Invoke();};
            bar.DragRequested+=Drag;footer.DragRequested+=Drag;
            frame.Controls.Add(View);
            Controls.Add(frame);Controls.Add(footer);Controls.Add(bar);
            View.SizeChanged+=(_,_)=>RoundView();
            SizeChanged+=(_,_)=>{var old=Region;using var path=DesktopCapture.Rounded(ClientRectangle,LoginVisible?22:0);Region=LoginVisible?new Region(path):null;old?.Dispose();};
            FormClosing+=(_,e)=>{if(!shutdown){e.Cancel=true;ReturnToBackground();UserReturned?.Invoke();}};
        }
        private void Drag(){Native.ReleaseCapture();Native.SendMessage(Handle,0xA1,2,0);}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if(!LoginVisible||ClientSize.Width<2||ClientSize.Height<2){base.OnPaintBackground(e);return;}
            if(backdrop==null||backdrop.Size!=ClientSize){backdrop?.Dispose();backdrop=LoginPaint.Backdrop(theme,ClientSize);}
            e.Graphics.DrawImageUnscaled(backdrop,0,0);
        }
        // Rounded corners for the web page itself, like every panel of the widget.
        private void RoundView()
        {
            if(!View.IsHandleCreated||View.Width<4||View.Height<4)return;
            try
            {
                var region=LoginVisible?Native.CreateRoundRectRgn(0,0,View.Width+1,View.Height+1,LoginViewFrame.Radius*2,LoginViewFrame.Radius*2):0;
                Native.SetWindowRgn(View.Handle,region,true);
            }
            catch(Exception error){AppPaths.Log("Sign-in page corners: "+error.GetType().Name);}
        }
        // Dark colour scheme for the login pages only; music pages keep their own.
        private void SetScheme(bool dark)
        {
            try
            {
                var profile=View.CoreWebView2?.Profile;
                if(profile!=null)profile.PreferredColorScheme=dark?CoreWebView2PreferredColorScheme.Dark:CoreWebView2PreferredColorScheme.Auto;
            }
            catch(Exception error){AppPaths.Log("Sign-in colour scheme: "+error.GetType().Name);}
        }
        internal void SetAddress(string url)
        {
            bar.Host=Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps?uri.Host:"";
        }
        internal void StartBackground()
        {
            Location=new(-32000,-32000);ClientSize=new(1280,900);bar.Visible=false;footer.Visible=false;Show();
        }
        internal void ShowLogin(bool autoHide)
        {
            AutoHideAfterLogin=autoHide;LoginVisible=true;
            theme=LoginTheme.For(design());   // the design picked in Settings -> Appearance
            bar.Theme=theme;footer.Theme=theme;
            backdrop?.Dispose();backdrop=null;
            BackColor=theme.Base;View.DefaultBackgroundColor=theme.Base;
            SetScheme(true);
            bar.Visible=true;footer.Visible=true;frame.Padding=new(16,2,16,0);
            ClientSize=new(1040,860);
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;
            Size=new(Math.Min(Width,Math.Max(320,area.Width-32)),Math.Min(Height,Math.Max(320,area.Height-32)));
            Location=new(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
            TopMost=true;Show();Activate();View.Focus();
            RoundView();Invalidate(true);
        }
        internal void ReturnToBackground()
        {
            LoginVisible=false;AutoHideAfterLogin=false;TopMost=false;Location=new(-32000,-32000);
            bar.Visible=false;footer.Visible=false;frame.Padding=Padding.Empty;ClientSize=new(1280,900);
            var old=Region;Region=null;old?.Dispose();
            RoundView();SetScheme(false);
        }
        internal void Shutdown(){if(shutdown)return;shutdown=true;backdrop?.Dispose();View.Dispose();Close();Dispose();}
    }
}
