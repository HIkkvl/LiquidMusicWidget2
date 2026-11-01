using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace LiquidMusic;

internal static class EmbeddedMusicTests
{
    internal static int Run(bool liveSite=false,bool services=false)
    {
        ApplicationConfiguration.Initialize();
        using var context=new ApplicationContext();
        using var start=new System.Windows.Forms.Timer{Interval=100};
        using var timeout=new System.Windows.Forms.Timer{Interval=120000};
        var code=1;
        timeout.Tick+=(_,_)=>{Write(new{ok=false,error="Embedded player test timed out"});context.ExitThread();};
        start.Tick+=async(_,_)=>
        {
            start.Stop();
            try{if(services)await ServicePlayerTests.CheckHostsAsync();else await CheckAsync(liveSite);code=0;}
            catch(Exception error){AppPaths.Log(error);Write(new{ok=false,error=error.ToString()});}
            finally{timeout.Stop();context.ExitThread();}
        };
        start.Start();timeout.Start();Application.Run(context);return code;
    }
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    private static void Write(object result)
    {
        Directory.CreateDirectory(AppPaths.Logs);
        File.WriteAllText(Path.Combine(AppPaths.Logs,"embedded-selftest.json"),JsonSerializer.Serialize(result));
    }
    private static async Task CheckAsync(bool liveSite)
    {
        var checks=new List<string>();
        Check(EmbeddedMusicHost.AllowedNavigation("https://passport.yandex.ru/auth","yandex"),"Yandex login allowed");
        Check(!EmbeddedMusicHost.AllowedNavigation("https://yandex.ru.evil.example/auth","yandex"),"Domain boundary");
        Check(!EmbeddedMusicHost.AllowedNavigation("http://music.yandex.kz/","yandex"),"HTTPS boundary");
        Check(!EmbeddedMusicHost.AllowedNavigation("https://liquid.local/","yandex"),"Remote player cannot navigate to native bridge");
        foreach(var domain in new[]{"ru","kz","com"})
        {
            var origin="https://music.yandex."+domain+"/";
            var login=new Uri(EmbeddedMusicHost.LoginUrl("yandex",origin));
            Check(login.Host=="passport.yandex."+domain&&login.AbsolutePath=="/auth","Sign-in domain matches selected Music domain");
            Check(Uri.UnescapeDataString(login.Query)=="?retpath="+origin,"Sign-in preserves regional music destination");
            var switchAccount=new Uri(EmbeddedMusicHost.LoginUrl("yandex",origin,true));
            Check(switchAccount.Host==login.Host&&switchAccount.AbsolutePath=="/auth/add","Account switching preserves selected domain");
        }
        Check(new Uri(EmbeddedMusicHost.LoginUrl("yandex","https://music.yandex.kz/",true)).AbsolutePath=="/auth/add","Connected account opens account switcher");
        var testRoot=Path.Combine(AppPaths.Data,"embedded-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var settings=new Settings(Path.Combine(testRoot,"settings.json"));
        settings.Value["yandex_domain"]="music.yandex.kz";settings.Save();
        var foreground=Native.GetForegroundWindow();
        var legacyInstall=Path.Combine(testRoot,"legacy-build");
        using var host=new EmbeddedMusicHost(Path.Combine(legacyInstall,".native-data","music-webview"));
        await host.InitializeAsync();
        using var playwright=await Playwright.CreateAsync();
        var transport=await playwright.Chromium.ConnectOverCDPAsync(host.Endpoint,new(){NoDefaults=true,Timeout=10000});
        var page=transport.Contexts.SelectMany(c=>c.Pages).Single();
        if(!liveSite)
        {
            var audio=Convert.ToBase64String(VoiceAssistant.ToWave(new byte[96000]));
            var fixture="""
                <meta charset="utf-8"><div class="UserProfile_userName">Test account</div>
                <div class="PlayerBar_root"><span class="Meta_title">Embedded test</span><span class="Meta_artists">Local fixture</span>
                <button id="play" aria-label="Воспроизведение">Play</button></div>
                <audio id="audio" loop src="data:audio/wav;base64,AUDIO"></audio>
                <script>
                document.getElementById('play').addEventListener('click',async event=>{
                  if(!event.isTrusted)return;
                  const audio=document.getElementById('audio');
                  if(audio.paused){await audio.play();event.currentTarget?.setAttribute('aria-label','Пауза');document.getElementById('play').setAttribute('aria-label','Пауза');}
                  else{audio.pause();document.getElementById('play').setAttribute('aria-label','Воспроизведение');}
                });
                </script>
                """.Replace("AUDIO",audio);
            await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=fixture}));
        }
        await using var controller=new BrowserController(settings,host);
        await controller.InitializeConnectionAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        for(var i=0;i<80&&page.Url=="about:blank";i++)await Task.Delay(100);
        Check(BrowserController.MatchesService(page.Url,"yandex"),"Music page lives inside the app's own WebView2");
        Check(host.Windows.All(window=>!window.ShowInTaskbar&&!window.LoginVisible&&Screen.AllScreens.All(screen=>!screen.Bounds.IntersectsWith(window.Bounds))),"Engine windows stay outside every monitor and taskbar");
        Check(await page.EvaluateAsync<bool>("()=>typeof window.liquid==='undefined'"),"No native widget API in remote music content");
        var port=new Uri(host.Endpoint).Port;
        Check(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(endpoint=>endpoint.Port==port).All(endpoint=>IPAddress.IsLoopback(endpoint.Address)),"Debug transport is loopback only");
        checks.Add("Isolated WebView2, hidden host, HTTPS/login allowlist, no widget bridge, loopback transport");
        if(liveSite)
        {
            await page.WaitForFunctionAsync("()=>!!document.querySelector('[class*=Vibe], [class*=PlayerBar]')",null,new(){Timeout=45000});
            var snapshot=await controller.SnapshotAsync("yandex");
            checks.Add("Yandex Music loads inside hidden WebView2; fresh profile needs user sign-in");
            await page.GotoAsync(EmbeddedMusicHost.LoginUrl("yandex","https://music.yandex.kz/"),new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=45000});
            Check(new Uri(page.Url).Host=="passport.yandex.kz","Official regional Yandex ID page opens directly");
            await page.Locator("input:visible:not([type=hidden])").First.WaitForAsync(new(){Timeout=30000});
            checks.Add("Official Yandex ID sign-in form loads directly with regional return address");
            Write(new{ok=true,checks,service="yandex",signedIn=snapshot["signed_in"]?.GetValue<bool>()==true});return;
        }
        await page.WaitForSelectorAsync("#play");
        var initial=await controller.SnapshotAsync("yandex");
        Check(initial["connected"]?.GetValue<bool>()==true,"Embedded account state reaches widget");
        var playing=await controller.CommandAsync("yandex","play");
        Check(playing["playing"]?.GetValue<bool>()==true,"Trusted play works in hidden WebView2");
        await Task.Delay(500);
        Check(await page.EvaluateAsync<bool>("()=>!document.getElementById('audio').paused&&document.getElementById('audio').currentTime>0"),"Audio advances while no player window is on screen");
        await controller.CommandAsync("yandex","volume",volume:.35);
        Check(await page.EvaluateAsync<bool>("()=>Math.abs(document.getElementById('audio').volume-.35)<.01"),"Widget volume controls internal audio");
        await controller.CommandAsync("yandex","pause");
        Check(await page.EvaluateAsync<bool>("()=>document.getElementById('audio').paused"),"Pause reaches internal audio");
        await controller.SuspendInactiveAsync("spotify");
        Check(host.Windows.Single().View.CoreWebView2.IsSuspended,"Inactive paused engine suspends its page");
        await host.EnsureServiceAsync("yandex","https://music.yandex.kz/");
        Check(!host.Windows.Single().View.CoreWebView2.IsSuspended,"Returning to a service resumes the same page");
        Check(await page.EvaluateAsync<bool>("()=>document.getElementById('audio').paused"),"Resume preserves playback state without reloading");
        Check(Native.GetForegroundWindow()==foreground,"Background playback never steals Windows foreground");
        checks.Add("Hidden audio playback, trusted play/pause, volume and Windows focus preservation");
        var waveFixture=File.ReadAllText(Path.Combine(AppPaths.Root,"tests","fixtures","yandex-wave-wheel.html"));
        await page.UnrouteAllAsync();
        await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=waveFixture}));
        await page.ReloadAsync();
        var genre=await controller.CommandAsync("yandex","wave","Русский рок");
        Check(genre["active_wave"]?.GetValue<string>()=="Русский рок","Embedded command exposes selected genre");
        var defaultWave=await controller.CommandAsync("yandex","wave_reset");
        Check(defaultWave["active_wave"]==null&&defaultWave["playing"]?.GetValue<bool>()==true,"Embedded reset clears genre while music keeps playing");
        checks.Add("Full embedded controller genre selection and default-wave reset");
        var returnFixture=File.ReadAllText(Path.Combine(AppPaths.Root,"tests","fixtures","yandex-wave-return.html"));
        await page.UnrouteAllAsync();
        await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=returnFixture}));
        await page.ReloadAsync();
        await controller.CommandAsync("yandex","wave","Русский рок");
        var modernReset=await controller.CommandAsync("yandex","wave_reset");
        Check(modernReset["active_wave"]==null&&modernReset["playing"]?.GetValue<bool>()==true,"Embedded reset uses current site's Return card");
        Check(await page.EvaluateAsync<bool>("()=>window.resetClicks===1&&window.heroClicks===0&&window.scrollCalls===0"),"Embedded reset does not pause current station or scroll the layout");
        checks.Add("Current My Wave return card clears the genre while the same song continues");
        await page.EvaluateAsync("()=>{document.cookie='embeddedSessionProbe=ok; Max-Age=3600; Secure; SameSite=Lax';}");
        await page.ReloadAsync();
        Check(await page.EvaluateAsync<bool>("()=>document.cookie.includes('embeddedSessionProbe=ok')"),"Separate music profile retains session cookies");
        Check(controller.Status()["embedded"]?.GetValue<bool>()==true,"Default Windows browser is not used");
        checks.Add("Persistent app profile cookies and embedded account status");
        var processId=(int)host.Windows.First().View.CoreWebView2.BrowserProcessId;
        await controller.DisposeAsync();
        await Task.Run(()=>{try{System.Diagnostics.Process.GetProcessById(processId).WaitForExit(5000);}catch(ArgumentException){}});
        var sharedProfile=ProfileStorage.Prepare("music-webview",Path.Combine(testRoot,"shared-profiles"),new[]{legacyInstall});
        using var restored=new EmbeddedMusicHost(sharedProfile);
        await restored.InitializeAsync();
        var restoredTransport=await playwright.Chromium.ConnectOverCDPAsync(restored.Endpoint,new(){NoDefaults=true,Timeout=10000});
        var restoredPage=restoredTransport.Contexts.SelectMany(c=>c.Pages).Single();
        await restoredPage.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html",Body="<p>restored app profile</p>"}));
        await restored.EnsureServiceAsync("yandex","https://music.yandex.kz/");
        await restoredPage.WaitForSelectorAsync("p");
        Check(await restoredPage.EvaluateAsync<bool>("()=>document.cookie.includes('embeddedSessionProbe=ok')"),"Session cookie survives destruction and recreation of the music engine");
        Check(Native.GetForegroundWindow()==foreground,"Restoring saved music session stays invisible");
        checks.Add("Encrypted persistent login cookie survives migration from an old build and music-engine restart");
        await restored.ShowLoginAsync("yandex","https://music.yandex.kz/");
        await restoredPage.WaitForURLAsync("https://passport.yandex.kz/auth?**");
        await restored.EnsureServiceAsync("yandex","https://music.yandex.kz/");
        Check(new Uri(restoredPage.Url).Host=="passport.yandex.kz","Widget polling does not navigate away from regional sign-in");
        var loginWindow=restored.Windows.Single();
        var workArea=Screen.FromControl(loginWindow).WorkingArea;
        Check(loginWindow.LoginVisible&&loginWindow.Width==Math.Min(1040,Math.Max(320,workArea.Width-32)),"Sign-in uses enlarged window fitted to monitor");
        Check(workArea.Contains(loginWindow.Bounds)&&loginWindow.Region!=null,"Rounded sign-in stays within working area");
        restored.SetSignedIn("yandex",true);
        Check(!loginWindow.LoginVisible&&Screen.AllScreens.All(screen=>!screen.Bounds.IntersectsWith(loginWindow.Bounds)),"Successful sign-in restores hidden widget engine");
        checks.Add("Direct regional sign-in, enlarged rounded window, polling preservation and automatic return to widget");
        Write(new{ok=true,checks});
    }
}
