using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace LiquidMusic;

internal sealed class MainForm : Form
{
    private readonly Settings settings=new();
    private readonly WebView2 view=new(){Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(11,22,40)};
    private readonly BrowserController browser=new();
    private readonly VoiceAssistant voice;
    private readonly NotifyIcon tray=new();
    private readonly System.Windows.Forms.Timer poll=new(){Interval=1200};
    private readonly System.Windows.Forms.Timer wheelTimer=new(){Interval=90};
    private readonly System.Windows.Forms.Timer islandTimer=new(){Interval=120};
    private readonly NoActivatePopup popup=new();
    private readonly NoActivatePopup island=new();
    private GlobalHotkeys? hotkeys;
    private WheelControl? wheel;
    private JsonObject snapshot=new(){["connected"]=false,["title"]="В ожидании музыки",["artist"]="Подключите браузер в настройках"};
    private string hotkeyError="";
    private bool quitting,polling;
    private double? wheelTarget;
    private readonly bool startHidden;
    private readonly bool uiTest;
    private readonly EventWaitHandle showSignal=new(false,EventResetMode.AutoReset,"Local\\LiquidMusicWidget.Native.Show");
    private RegisteredWaitHandle? showWait;
    private readonly CancellationTokenSource lifetime=new();
    protected override bool ShowWithoutActivation => true;
    public MainForm(bool hidden,bool test=false)
    {
        uiTest=test;
        if(uiTest)_=Task.Run(async()=>{await Task.Delay(30000);if(!quitting&&!IsDisposed&&IsHandleCreated)BeginInvoke(()=>{File.WriteAllText(Path.Combine(AppPaths.Logs,"ui-selftest.json"),"{\"ok\":false,\"error\":\"WebView initialization timeout\"}");Environment.ExitCode=1;Quit();});});
        startHidden=hidden;
        Text="Liquid Music — C#";Size=new(414,1000);MinimumSize=new(350,600);
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;
        TopMost=settings.Value["on_top"]?.GetValue<bool>()??true;
        StartPosition=FormStartPosition.Manual;
        var area=Screen.PrimaryScreen!.WorkingArea;Size=new(Math.Min(414,area.Width-40),Math.Min(1000,area.Height-64));Location=new(area.Right-Width-28,area.Top+28);
        Controls.Add(view);
        var icon=Path.Combine(AppContext.BaseDirectory,"ui","assets","app.ico");
        Icon=File.Exists(icon)?new Icon(icon):SystemIcons.Application;
        voice=new(command=>OnUiAsync(async()=>{if(uiTest){snapshot=new(){["title"]="Тестовый трек",["connected"]=true};return;}var service=settings.Service;var result=await browser.CommandAsync(service,command.Action,command.Query);snapshot=result;Notify(command.Action,result);}),uiTest?(_=>Task.FromResult("следующий трек")):null,uiTest?(_=>Task.CompletedTask):null);
        voice.Changed+=()=>{if(!IsDisposed&&IsHandleCreated)BeginInvoke(()=>VoiceChanged());};
        Load+=async(_,_)=>await InitializeAsync();
        Shown+=(_,_)=>{if(startHidden)Hide();};
        FormClosing+=(_,e)=>{if(!quitting){e.Cancel=true;Hide();}};
        poll.Tick+=async(_,_)=>await PollAsync();
        wheelTimer.Tick+=async(_,_)=>{var value=wheelTarget;wheelTarget=null;if(value!=null)await RunActionAsync("volume",value.Value);};
        islandTimer.Tick+=(_,_)=>UpdateIsland();
        island.Command+=async action=>await RunActionAsync(action);
    }
    private async Task InitializeAsync()
    {
        try
        {
            hotkeys=new(Handle);
            if(!uiTest)try{hotkeys.Apply(settings.Value["bindings"]!.AsObject());}catch(Exception e){hotkeyError=e.Message;AppPaths.Log(e);}
            if(!uiTest)wheel=new(){Options=settings.Value["wheel"]};
            if(wheel!=null)wheel.Changed+=delta=>
            {
                if(snapshot["connected"]?.GetValue<bool>()!=true||snapshot["volume"]==null)return;
                wheelTarget=Math.Clamp((wheelTarget??snapshot["volume"]!.GetValue<double>())+delta,0,1);
            };
            var menu=new ContextMenuStrip();
            menu.Items.Add("Открыть виджет",null,(_,_)=>ShowWidget());
            menu.Items.Add("Скрыть виджет",null,(_,_)=>Hide());
            menu.Items.Add("Голосовая команда",null,(_,_)=>StartVoice());
            menu.Items.Add("Пауза / продолжить",null,async(_,_)=>await RunActionAsync("play_pause"));
            menu.Items.Add("Следующий трек",null,async(_,_)=>await RunActionAsync("next"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Подключить браузер…",null,async(_,_)=>{try{await SetupBrowserFromUiAsync(true);}catch(Exception e){MessageBox.Show(this,e.Message,"Liquid Music");}});
            menu.Items.Add("Выйти",null,(_,_)=>Quit());
            tray.Icon=Icon;tray.Text="Liquid Music — C#";tray.ContextMenuStrip=menu;tray.Visible=true;
            tray.DoubleClick+=(_,_)=>ShowWidget();
            showWait=ThreadPool.RegisterWaitForSingleObject(showSignal,(_,_)=>{if(!IsDisposed)BeginInvoke(ShowWidget);},null,Timeout.Infinite,false);
            Directory.CreateDirectory(AppPaths.Data);
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(AppPaths.Data,"widget-webview"));
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("liquid.local",Path.Combine(AppContext.BaseDirectory,"ui"),CoreWebView2HostResourceAccessKind.DenyCors);
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            view.CoreWebView2.Settings.AreDevToolsEnabled=false;
            view.CoreWebView2.NewWindowRequested+=(_,e)=>e.Handled=true;
            view.CoreWebView2.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith("https://liquid.local/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};
            view.CoreWebView2.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            view.CoreWebView2.WebMessageReceived+=ReceiveMessage;
            view.CoreWebView2.ProcessFailed+=(_,e)=>AppPaths.Log("WebView process failed: "+e.ProcessFailedKind);
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
            view.CoreWebView2.Navigate("https://liquid.local/index.html");
            if(!uiTest)await browser.InitializeConnectionAsync();
            poll.Start();wheelTimer.Start();islandTimer.Start();
            await PollAsync();
            if(!uiTest)File.WriteAllText(Path.Combine(AppPaths.Logs,"native-startup.json"),JsonSerializer.Serialize(new{ok=true,hotkeyError,voiceKey=settings.Value["bindings"]?["voice"]?["key"]?.GetValue<string>(),browserConnected=snapshot["connected"]?.GetValue<bool>()==true,browser=browser.Status()}));
            if(uiTest)_=UiTestAsync();
        }
        catch(Exception e){AppPaths.Log(e);MessageBox.Show(e.Message,"Liquid Music — ошибка запуска",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
    private async Task UiTestAsync()
    {
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(15);string result="null";
            while(DateTime.UtcNow<deadline)
            {
                await Task.Delay(300);
                result=await view.ExecuteScriptAsync("(()=>({bridge:!!window.liquid?.api,ready:!!configuration?.bindings,rows:document.querySelectorAll('.genre').length,voice:!!document.getElementById('voice-button'),native:configuration?.backend==='csharp'}))()");
                var data=JsonNode.Parse(result);
                if(data?["ready"]?.GetValue<bool>()==true)
                {
                    if(data["rows"]?.GetValue<int>()!=14||data["native"]?.GetValue<bool>()!=true)throw new Exception("Native UI bridge returned unexpected state: "+result);
                    var foreground=Native.GetForegroundWindow();
                    StartVoice();
                    var voiceDeadline=DateTime.UtcNow.AddSeconds(3);
                    while(voice.Busy&&DateTime.UtcNow<voiceDeadline)await Task.Delay(50);
                    if(voice.Snapshot()["phase"]?.GetValue<string>()!="done")throw new Exception("Voice pipeline did not finish");
                    if(Visible||Native.GetForegroundWindow()!=foreground)throw new Exception("Voice command changed application visibility or Windows foreground");
                    File.WriteAllText(Path.Combine(AppPaths.Logs,"ui-selftest.json"),JsonSerializer.Serialize(new{ok=true,result=data,voiceRemainsHidden=true,foregroundPreserved=true}));Quit();return;
                }
            }
            throw new Exception("Native UI bridge did not initialize: "+result);
        }
        catch(Exception e){AppPaths.Log(e);File.WriteAllText(Path.Combine(AppPaths.Logs,"ui-selftest.json"),JsonSerializer.Serialize(new{ok=false,error=e.ToString()}));Environment.ExitCode=1;Quit();}
    }
    private const string BridgeScript="""
    (()=>{
      if(location.origin!=='https://liquid.local')return;
      let sequence=0;const pending=new Map();
      chrome.webview.addEventListener('message',event=>{const data=event.data;const call=pending.get(data.id);if(!call)return;pending.delete(data.id);clearTimeout(call.timer);call.resolve(data.result);});
      window.liquid={api:new Proxy({}, {get:(_,method)=>(...args)=>new Promise(resolve=>{
        const id=++sequence;const timer=setTimeout(()=>{pending.delete(id);resolve({ok:false,error:'Приложение не ответило вовремя'});},90000);
        pending.set(id,{resolve,timer});chrome.webview.postMessage({id,method,args});
      })})};
      window.addEventListener('DOMContentLoaded',()=>{
        document.querySelector('.brand')?.addEventListener('pointerdown',e=>{if(e.button===0)chrome.webview.postMessage({id:0,method:'drag_window',args:[]});});
        window.dispatchEvent(new Event('liquidready'));
      });
    })();
    """;
    private async void ReceiveMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(e.Source is not "https://liquid.local/index.html")return;
        long id=0;
        object? result;
        try
        {
            var message=JsonNode.Parse(e.WebMessageAsJson)!;id=message["id"]!.GetValue<long>();
            result=await InvokeApiAsync(message["method"]!.GetValue<string>(),message["args"]!.AsArray());
        }
        catch(Exception error){AppPaths.Log(error);result=new{ok=false,error=error.Message};}
        if(!IsDisposed&&view.CoreWebView2!=null)view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,result}));
    }
    private object State()=>new JsonObject(snapshot.Select(pair=>KeyValuePair.Create(pair.Key,pair.Value?.DeepClone())))
    {
        ["voice"]=voice.Snapshot(),["quality"]=settings.Value["quality"]?.DeepClone(),["service"]=settings.Service,["browser"]=browser.Status()
    };
    private object Configuration()
    {
        var result=(JsonObject)settings.Value.DeepClone();
        result["actions"]=JsonSerializer.SerializeToNode(Settings.Actions);result["keys"]=JsonSerializer.SerializeToNode(GlobalHotkeys.Keys.Keys);
        result["genres"]=JsonSerializer.SerializeToNode(Settings.Genres);result["startup"]=StartupEnabled();result["hotkey_error"]=hotkeyError;
        result["backend"]="csharp";
        result["browser"]=browser.Status();
        return result;
    }
    private async Task<object?> InvokeApiAsync(string method,JsonArray args)
    {
        JsonNode? Arg(int index)=>index<args.Count?args[index]:null;
        switch(method)
        {
            case "get_state":return State();
            case "get_settings":return Configuration();
            case "get_browser_status":return browser.Status();
            case "get_drop":return settings.Value["drop"]!.DeepClone();
            case "show":ShowWidget();break;
            case "hide":Hide();break;
            case "quit":BeginInvoke(Quit);break;
            case "drag_window":Native.ReleaseCapture();Native.SendMessage(Handle,0xA1,2,0);break;
            case "start_voice":StartVoice();break;
            case "cancel_voice":voice.Cancel();break;
            case "command":
                var action=Arg(0)!.GetValue<string>();
                await RunCommandAsync(action,Arg(1));break;
            case "open_music":await RunCommandAsync("open",null);break;
            case "setup_browser":
                if(!await SetupBrowserFromUiAsync(Arg(0)?.GetValue<bool>()??true))return new{ok=false,cancelled=true,browser=browser.Status()};
                return new{ok=true,browser=browser.Status()};
            case "set_service":
                var service=Arg(0)!.GetValue<string>();if(service is not("yandex" or "youtube"))throw new ArgumentException("Неизвестный сервис");
                settings.Value["service"]=service;settings.Save();snapshot=new(){["connected"]=false,["title"]="Подключение…"};await PollAsync();break;
            case "set_quality":
                var quality=Arg(0)!.GetValue<string>();if(quality is not("high" or "medium" or "low"))throw new ArgumentException("Неизвестное качество");
                settings.Value["quality"]=quality;settings.Save();break;
            case "set_topmost":TopMost=Arg(0)!.GetValue<bool>();settings.Value["on_top"]=TopMost;settings.Save();break;
            case "set_startup":SetStartup(Arg(0)!.GetValue<bool>());break;
            case "set_fx":settings.Value["fx"]=Arg(0)!.DeepClone();settings.Save();return new{ok=true};
            case "get_backdrop":return new{ok=false};
            case "save_drop":
                var drop=Arg(0)!.AsObject();if(drop["side"]?.GetValue<string>() is not("top" or "bottom" or "left" or "right"))throw new ArgumentException("Неизвестный край экрана");
                settings.Value["drop"]=drop.DeepClone();settings.Save();break;
            case "save_settings":SaveSettings(args);break;
            case "preview_overlay":Notify("preview",snapshot,true);break;
            case "get_artwork":
                var url=Arg(0)?.GetValue<string>()??"";
                if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!(uri.Host.EndsWith(".yandex.net")||uri.Host.EndsWith(".ytimg.com")||uri.Host.EndsWith(".googleusercontent.com")||uri.Host.EndsWith(".ggpht.com")))return new{ok=false};
                using(var http=new HttpClient{Timeout=TimeSpan.FromSeconds(8)})
                using(var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead))
                {
                    if(response.Content.Headers.ContentLength>3_000_000)return new{ok=false};
                    var bytes=await response.Content.ReadAsByteArrayAsync();
                    if(bytes.Length>3_000_000)return new{ok=false};
                    return new{ok=true,data="data:"+(response.Content.Headers.ContentType?.MediaType??"image/jpeg")+";base64,"+Convert.ToBase64String(bytes)};
                }
            default:throw new ArgumentException("Неизвестный метод приложения");
        }
        return new{ok=true};
    }
    private void SaveSettings(JsonArray args)
    {
        var bindings=args[0]!.AsObject();var old=(JsonObject)settings.Value["bindings"]!.DeepClone();
        if(args.Count>1&&args[1] is JsonObject overlay)
        {
            if(overlay["corner"]?.GetValue<string>() is not("top-left" or "top-right" or "bottom-left" or "bottom-right"))throw new ArgumentException("Неизвестный угол уведомления");
            var duration=overlay["duration"]?.GetValue<double>()??4;
            if(duration<1||duration>15)throw new ArgumentException("Время показа должно быть от 1 до 15 секунд");
        }
        if(args.Count>2&&args[2] is JsonObject wheelOptions)
        {
            if(wheelOptions["modifier"]?.GetValue<string>() is not("ctrl" or "alt" or "shift" or "none"))throw new ArgumentException("Неизвестная клавиша колёсика");
            var step=wheelOptions["step"]?.GetValue<double>()??5;if(step<1||step>25)throw new ArgumentException("Неверный шаг громкости");
        }
        hotkeys!.Apply(bindings);
        var previous=(JsonObject)settings.Value.DeepClone();
        try
        {
            settings.Value["bindings"]=bindings.DeepClone();
            if(args.Count>1&&args[1]!=null)settings.Value["overlay"]=args[1]!.DeepClone();
            if(args.Count>2&&args[2]!=null)settings.Value["wheel"]=args[2]!.DeepClone();
            settings.Save();hotkeyError="";if(wheel!=null)wheel.Options=settings.Value["wheel"];
        }
        catch{hotkeys.Apply(old);foreach(var pair in previous)settings.Value[pair.Key]=pair.Value?.DeepClone();throw;}
    }
    private async Task PollAsync()
    {
        if(polling||quitting)return;polling=true;
        try
        {
            var service=settings.Service;
            var next=await browser.SnapshotAsync(service);
            if(service==settings.Service&&next["busy"]==null)snapshot=next;
        }
        catch(Exception error){AppPaths.Log(error);}
        finally{polling=false;}
    }
    private async Task RunCommandAsync(string action,JsonNode? value)
    {
        var result=await browser.CommandAsync(settings.Service,action,action is "genre" or "search_play"?value?.GetValue<string>():null,action=="volume"?value?.GetValue<double>():null);
        snapshot=result;
        if(action is not("volume" or "genre" or "open"))Notify(action,result);
    }
    private async Task RunActionAsync(string action,double? volume=null)
    {
        try
        {
            if(action=="toggle"){if(Visible)Hide();else ShowWidget();return;}
            if(action=="voice"){StartVoice();return;}
            await RunCommandAsync(action,volume==null?null:JsonValue.Create(volume.Value));
        }
        catch(Exception e){AppPaths.Log(e);snapshot["error"]=e.Message;tray.Text="Liquid Music — ошибка (откройте виджет)";}
    }
    private void StartVoice()
    {
        // This path deliberately never calls Show, Activate, SetForegroundWindow,
        // native input injection or BringToFront on any window.
        if(voice.Busy)voice.Cancel();else voice.Start();
    }
    private void VoiceChanged()
    {
        var state=voice.Snapshot();var message=state["message"]?.GetValue<string>()??"";
        tray.Text=("Liquid Music — "+message)[..Math.Min(63,14+message.Length)];
        if(settings.Value["overlay"]?["enabled"]?.GetValue<bool>()==true)
        {
            popup.ShowMessage(state["phase"]?.GetValue<string>()=="listening"?"Микрофон включён":"Голосовая команда",message,settings.Value["overlay"]!,voice.Busy?15:4);
        }
    }
    private void Notify(string action,JsonObject data,bool force=false)
    {
        if(!force&&settings.Value["overlay"]?["enabled"]?.GetValue<bool>()!=true)return;
        popup.ShowMessage(data["title"]?.GetValue<string>()??"Liquid Music",data["artist"]?.GetValue<string>()??"Команда выполнена",settings.Value["overlay"]!,settings.Value["overlay"]?["duration"]?.GetValue<double>()??4);
    }
    private void UpdateIsland()
    {
        if(settings.Value["drop"]?["enabled"]?.GetValue<bool>()!=true){island.Hide();return;}
        var cursor=Cursor.Position;var area=Screen.FromPoint(cursor).WorkingArea;
        var side=settings.Value["drop"]?["side"]?.GetValue<string>()??"top";
        var distance=side switch{"top"=>Math.Abs(cursor.Y-area.Top),"bottom"=>Math.Abs(cursor.Y-area.Bottom),"left"=>Math.Abs(cursor.X-area.Left),_=>Math.Abs(cursor.X-area.Right)};
        if(distance<=3)
        {
            island.Size=new(340,110);
            island.Location=side switch{"top"=>new(area.Left+(area.Width-island.Width)/2,area.Top+5),"bottom"=>new(area.Left+(area.Width-island.Width)/2,area.Bottom-island.Height-5),"left"=>new(area.Left+5,area.Top+(area.Height-island.Height)/2),_=>new(area.Right-island.Width-5,area.Top+(area.Height-island.Height)/2)};
            island.SetTrack(snapshot);
            if(!island.Visible)island.Show();
        }
        else if(island.Visible&&!Rectangle.Inflate(island.Bounds,35,35).Contains(cursor))island.Hide();
    }
    private async Task<bool> SetupBrowserFromUiAsync(bool updateShortcuts)
    {
        var info=BrowserSetup.DetectDefault();
        if(!info.Supported)throw new InvalidOperationException(info.Error);
        var status=browser.Status();
        if(status["connected"]?.GetValue<bool>()==true&&(!updateShortcuts||status["shortcuts_updated"]?.GetValue<bool>()==true))return true;
        var note=info.Name+" — браузер по умолчанию Windows. Настройка выполняется один раз и сохраняется. Если потребуется перезапуск браузера, вкладки будут восстановлены; сначала сохраните ввод на страницах. Локальный канал даёт приложению доступ к вкладкам.";
        if(updateShortcuts)note+=" Ярлыки этого браузера будут обновлены для постоянного подключения; перед изменением сохраняются резервные копии.";
        if(MessageBox.Show(this,note+" Продолжить?","Подключить браузер",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes)return false;
        await browser.ConfigureAsync(updateShortcuts);await PollAsync();return true;
    }
    private static bool StartupEnabled()
    {
        using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("LiquidMusicWidget")!=null;
    }
    private static void SetStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(enabled)key.SetValue("LiquidMusicWidget",'"'+Environment.ProcessPath+'"'+" --hidden");else key.DeleteValue("LiquidMusicWidget",false);
    }
    private void ShowWidget(){Show();WindowState=FormWindowState.Normal;Activate();}
    private Task OnUiAsync(Func<Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(IsDisposed){completion.SetCanceled();return completion.Task;}
        BeginInvoke(async()=>{try{await action();completion.SetResult();}catch(Exception e){completion.SetException(e);}});
        return completion.Task;
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x0312&&hotkeys?.Action((int)m.WParam) is string action)_=RunActionAsync(action);
        base.WndProc(ref m);
    }
    private async void Quit()
    {
        if(quitting)return;quitting=true;lifetime.Cancel();poll.Stop();wheelTimer.Stop();islandTimer.Stop();
        voice.Dispose();hotkeys?.Dispose();wheel?.Dispose();showWait?.Unregister(null);showSignal.Dispose();tray.Visible=false;tray.Dispose();popup.Dispose();island.Dispose();
        await browser.DisposeAsync();Close();Application.Exit();
    }
}

internal sealed class NoActivatePopup : Form
{
    private string heading="",body="";
    private bool transport;
    private readonly System.Windows.Forms.Timer hideTimer=new();
    private readonly Font headingFont=new("Segoe UI",12,FontStyle.Bold);
    private readonly Font bodyFont=new("Segoe UI",10);
    public event Action<string>? Command;
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
    public NoActivatePopup()
    {
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;BackColor=Color.FromArgb(18,22,32);ForeColor=Color.White;Size=new(360,100);DoubleBuffered=true;
        hideTimer.Tick+=(_,_)=>{hideTimer.Stop();Hide();};
    }
    public void SetTrack(JsonObject track){transport=true;heading=track["title"]?.GetValue<string>()??"В ожидании музыки";body=track["artist"]?.GetValue<string>()??"";Invalidate();}
    public void ShowMessage(string title,string message,JsonNode options,double seconds)
    {
        transport=false;heading=title;body=message;Size=new(390,105);
        var area=Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
        var corner=options["corner"]?.GetValue<string>()??"top-right";
        Location=new(corner.EndsWith("right")?area.Right-Width-18:area.Left+18,corner.StartsWith("bottom")?area.Bottom-Height-18:area.Top+18);
        Invalidate();Show();hideTimer.Stop();hideTimer.Interval=(int)(Math.Clamp(seconds,1,30)*1000);hideTimer.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics,heading,headingFont,new Rectangle(16,12,Width-32,26),ForeColor,TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics,body,bodyFont,new Rectangle(16,42,Width-32,transport?22:Height-48),Color.LightGray,TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
        if(transport)TextRenderer.DrawText(e.Graphics,"⏮          ■          ▶ / Ⅱ          ⏭",Font,new Rectangle(16,76,Width-32,25),Color.LightSkyBlue);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if(transport&&e.Y>70)Command?.Invoke(Math.Clamp((e.X-16)*4/Math.Max(1,Width-32),0,3) switch{0=>"previous",1=>"stop",2=>"play_pause",_=>"next"});
        base.OnMouseDown(e);
    }
    protected override void WndProc(ref Message m){if(m.Msg==0x21){m.Result=3;return;}base.WndProc(ref m);}
    protected override void Dispose(bool disposing){if(disposing){hideTimer.Dispose();headingFont.Dispose();bodyFont.Dispose();}base.Dispose(disposing);}
}
