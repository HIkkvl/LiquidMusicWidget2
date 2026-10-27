using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Vosk;

namespace LiquidMusic;

internal static class SelfTests
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public static int Run()
    {
        var passed=new List<string>();
        try
        {
            Check(VoiceParser.Parse("Включи трек Династия!")==new VoiceCommand("search_play","Династия"),"Russian track parser");
            Check(VoiceParser.Parse("поставь на паузу").Action=="pause","Pause parser");
            Check(VoiceParser.Parse("следующий трек").Action=="next","Next parser");
            try{VoiceParser.Parse("включи трек");throw new Exception("Empty title accepted");}catch(ArgumentException){}
            passed.Add("Russian commands and empty-query validation");
            var bindings=Settings.Defaults();GlobalHotkeys.Validate(bindings);
            bindings["like"]=bindings["next"]!.DeepClone();
            try{GlobalHotkeys.Validate(bindings);throw new Exception("Duplicate shortcuts accepted");}catch(ArgumentException){}
            bindings=Settings.Defaults();bindings["toggle"]!["key"]="";
            try{GlobalHotkeys.Validate(bindings);throw new Exception("Disabled toggle accepted");}catch(ArgumentException){}
            passed.Add("Shortcut conflict and required toggle validation");
            var file=Path.Combine(AppPaths.Data,"selftest-settings.json");Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(file,"{\"bindings\":{\"voice\":{\"key\":\"End\",\"ctrl\":false,\"alt\":false,\"shift\":false}},\"service\":\"youtube\"}");
            var settings=new Settings(file);settings.Save();
            Check(new Settings(file).Value["bindings"]!["voice"]!["key"]!.GetValue<string>()=="End","Settings migration");
            Check(settings.Service=="youtube","Service preservation");File.Delete(file);
            passed.Add("Existing JSON migration and atomic roundtrip");
            Check(BrowserController.MatchesService("https://music.yandex.kz/search?text=1","yandex"),"Regional Yandex");
            Check(!BrowserController.MatchesService("https://music.yandex.kz.evil.example/","yandex"),"Domain boundary");
            Check(!BrowserController.MatchesService("http://music.yandex.kz/","yandex"),"HTTPS boundary");
            Check(BrowserController.MatchesService("https://music.youtube.com/watch?v=x","youtube"),"YouTube Music");
            passed.Add("Browser service allowlist");
            Check(BrowserSetup.ParseExecutable("\"C:\\Program Files\\BraveSoftware\\brave.exe\" --single-argument %1")==@"C:\Program Files\BraveSoftware\brave.exe","Default-browser executable parsing");
            Check(BrowserSetup.Identify(@"C:\Browser\chrome.exe").Name=="Google Chrome","Chrome detection");
            Check(!BrowserSetup.Identify(@"C:\Browser\firefox.exe").Supported,"Unsupported browser does not fall back to Brave");
            var arguments=BrowserShortcuts.WithControlArguments("--profile-directory=\"Profile 1\" --remote-debugging-port=9222 --remote-debugging-address=0.0.0.0",54321);
            Check(arguments.Contains("--profile-directory=\"Profile 1\"")&&arguments.Contains("--remote-debugging-port=54321")&&!arguments.Contains("0.0.0.0")&&!arguments.Contains("9222"),"Persistent shortcut arguments");
            Check(BrowserShortcuts.WithControlArguments(arguments,54321)==arguments,"Shortcut setup is idempotent");
            passed.Add("Windows default-browser parsing, unsupported browser, shortcut preservation/idempotence");
            Check(BrowserController.VoiceMatch(new(){["title"]="ДИНАСТИЯ",["artist"]="VILLIAN, madk1d"},"Династия"),"Playback confirmation");
            Check(!BrowserController.VoiceMatch(new(){["title"]="Династия Ди"},"Династия Исполнитель"),"Wrong track rejected");
            passed.Add("Confirmed track matching");
            Vosk.Vosk.SetLogLevel(-1);
            using(var model=new Model(AppPaths.Model))
            using(var recognizer=new VoskRecognizer(model,16000))
            {
                var silence=new byte[32000];recognizer.AcceptWaveform(silence,silence.Length);
                Check(JsonNode.Parse(recognizer.FinalResult())?["text"]?.GetValue<string>()=="","Offline native speech model");
            }
            passed.Add("Vosk native x64 model loads and decodes PCM locally");
            Write("selftest.json",new{ok=true,passed});return 0;
        }
        catch(Exception error){AppPaths.Log(error);Write("selftest.json",new{ok=false,passed,error=error.ToString()});return 1;}
    }
    public static async Task<int> BrowserAsync()
    {
        var passed=new List<string>();
        try
        {
            using var playwright=await Playwright.CreateAsync();
            await using var browser=await playwright.Chromium.LaunchAsync(new(){Channel="msedge",Headless=true});
            var page=await browser.NewPageAsync();
            var cards=File.ReadAllText(Path.Combine(AppPaths.Root,"tests","fixtures","yandex-search-cards.html"));
            var fixture="<meta charset='utf-8'><div class='PlayerBar_root'><span class='Meta_title'>Старый трек</span><span class='Meta_artists'>VILLIAN, madk1d</span><button aria-label='Пауза'></button></div>"+cards;
            await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=fixture}));
            await page.GotoAsync("https://music.yandex.kz/search?text=Династия");
            var script=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"BrowserScripts.js"));
            foreach(var domain in new[]{"music.yandex.ru","music.yandex.kz","music.yandex.com"})
            {
                await page.GotoAsync("https://"+domain+"/search?text=Династия");
                Check(await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query="Династия"}),"Regional track controls: "+domain);
            }
            passed.Add("Track controls support ru, kz and com regional domains");
            var target=await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query="Династия"});
            Check(target,"Real Yandex track card play target");
            Check(await page.Locator("[data-liquid-control='search']").EvaluateAsync<bool>("el=>!!el.closest('[class*=EntityCard_root]').querySelector('a[href*=\"/track/\"]')"),"Artist exclusion");
            await page.Locator("[data-liquid-control='search']").EvaluateAsync("el=>el.addEventListener('click',()=>{window.clicked=true;document.querySelector('.Meta_title').textContent='Династия';})");
            await page.Locator("[data-liquid-control='search']").ClickAsync(new(){Force=true});
            Check(await page.EvaluateAsync<bool>("()=>window.clicked===true"),"CDP browser click");
            passed.Add("Real Yandex DOM and browser input without extension");
            var state=await page.EvaluateAsync<System.Text.Json.JsonElement>(script,new{action="snapshot",service="yandex"});
            Check(state.GetProperty("title").GetString()=="Династия","Player metadata");
            passed.Add("Browser player metadata");
            var foreground=Native.GetForegroundWindow();
            await page.EvaluateAsync("()=>document.title='background test'");
            Check(Native.GetForegroundWindow()==foreground,"Background browser command changed foreground");
            passed.Add("Background browser evaluation preserves Windows foreground");
            Write("browser-selftest.json",new{ok=true,passed});return 0;
        }
        catch(Exception error){AppPaths.Log(error);Write("browser-selftest.json",new{ok=false,passed,error=error.ToString()});return 1;}
    }
    private static void Write(string file,object value)=>File.WriteAllText(Path.Combine(AppPaths.Logs,file),System.Text.Json.JsonSerializer.Serialize(value));
    public static async Task<int> LiveAsync()
    {
        var foreground=Native.GetForegroundWindow();
        static string App(nint window){Native.GetWindowThreadProcessId(window,out var id);try{return System.Diagnostics.Process.GetProcessById((int)id).ProcessName;}catch{return "unknown";}}
        try
        {
            await using var controller=new BrowserController();
            var result=await controller.CommandAsync("yandex","search_play","Династия");
            Write("live-selftest.json",new{ok=true,foregroundPreserved=Native.GetForegroundWindow()==foreground,foregroundBefore=App(foreground),foregroundAfter=App(Native.GetForegroundWindow()),result});
            return 0;
        }
        catch(Exception error){AppPaths.Log(error);Write("live-selftest.json",new{ok=false,foregroundPreserved=Native.GetForegroundWindow()==foreground,error=error.ToString()});return 1;}
    }
    public static async Task DiagnoseAsync()
    {
        await using var controller=new BrowserController();
        Write("browser-diagnostic.json",await controller.DiagnoseAsync("yandex"));
    }
}
