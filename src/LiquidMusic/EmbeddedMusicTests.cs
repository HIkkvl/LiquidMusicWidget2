using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace LiquidMusic;

internal static class EmbeddedMusicTests
{
    internal static int Run(bool liveSite=false)
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
            try{await CheckAsync(liveSite);code=0;}
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
        var testRoot=Path.Combine(AppPaths.Data,"embedded-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var settings=new Settings(Path.Combine(testRoot,"settings.json"));
        settings.Value["yandex_domain"]="music.yandex.kz";settings.Save();
        var foreground=Native.GetForegroundWindow();
        using var host=new EmbeddedMusicHost(Path.Combine(testRoot,"profile"));
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
        Check(Native.GetForegroundWindow()==foreground,"Background playback never steals Windows foreground");
        checks.Add("Hidden audio playback, trusted play/pause, volume and Windows focus preservation");
        await page.EvaluateAsync("()=>{document.cookie='embeddedSessionProbe=ok; Max-Age=3600; Secure; SameSite=Lax';}");
        await page.ReloadAsync();
        Check(await page.EvaluateAsync<bool>("()=>document.cookie.includes('embeddedSessionProbe=ok')"),"Separate music profile retains session cookies");
        Check(controller.Status()["embedded"]?.GetValue<bool>()==true,"Default Windows browser is not used");
        checks.Add("Persistent app profile cookies and embedded account status");
        var processId=(int)host.Windows.First().View.CoreWebView2.BrowserProcessId;
        await controller.DisposeAsync();
        await Task.Run(()=>{try{System.Diagnostics.Process.GetProcessById(processId).WaitForExit(5000);}catch(ArgumentException){}});
        using var restored=new EmbeddedMusicHost(Path.Combine(testRoot,"profile"));
        await restored.InitializeAsync();
        var restoredTransport=await playwright.Chromium.ConnectOverCDPAsync(restored.Endpoint,new(){NoDefaults=true,Timeout=10000});
        var restoredPage=restoredTransport.Contexts.SelectMany(c=>c.Pages).Single();
        await restoredPage.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html",Body="<p>restored app profile</p>"}));
        await restored.EnsureServiceAsync("yandex","https://music.yandex.kz/");
        await restoredPage.WaitForSelectorAsync("p");
        Check(await restoredPage.EvaluateAsync<bool>("()=>document.cookie.includes('embeddedSessionProbe=ok')"),"Session cookie survives destruction and recreation of the music engine");
        Check(Native.GetForegroundWindow()==foreground,"Restoring saved music session stays invisible");
        checks.Add("Persistent login cookie survives music-engine restart");
        Write(new{ok=true,checks});
    }
}
