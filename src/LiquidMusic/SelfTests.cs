using System.Text.Json;
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
            foreach(var spoken in new[]{"первого мая","первое мая","первый май","одного мая","1-го мая","1го мая"})
                Check(BrowserController.VoiceMatch(new(){["title"]="1 мая",["artist"]="madk1d, CUPSIZE"},"madk1d "+spoken),"Spoken date matches numeric title: "+spoken);
            Check(MusicNames.Normalize("двадцать первого июня")==MusicNames.Normalize("21 июня"),"Compound spoken day");
            Check(MusicNames.Normalize("тридцать первого декабря")==MusicNames.Normalize("31 декабря"),"Last day of month");
            Check(!BrowserController.VoiceMatch(new(){["title"]="2 мая",["artist"]="madk1d"},"madk1d первого мая"),"Wrong date rejected");
            Check(!BrowserController.VoiceMatch(new(){["title"]="1 мая",["artist"]="OtherArtist"},"madk1d первого мая"),"Date normalization preserves artist requirement");
            Check(MusicNames.Normalize("первого снега")!=MusicNames.Normalize("1 снега"),"Do not convert ordinary words outside dates");
            Check(!BrowserController.VoiceMatch(new(){["title"]="1 мая"},"---"),"Empty normalized query rejected");
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
            await page.EvaluateAsync("()=>document.body.insertAdjacentHTML('beforeend',`<div data-test-track='rehab'><button aria-label='Воспроизведение'>Play</button><a href='/album/1/track/2'>РЕХАБ</a><a href='/artist/3'>madk1d</a></div>`)");
            Check(await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query="madk1d Rehab"}),"Mixed English artist and Cyrillic track title");
            Check(await page.Locator("[data-liquid-control='search']").EvaluateAsync<bool>("el=>el.parentElement.dataset.testTrack==='rehab'"),"Artist-qualified query picks the right track card");
            Check(!await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query="OtherArtist Rehab"}),"Do not ignore a mismatched artist");
            await page.EvaluateAsync("()=>document.body.insertAdjacentHTML('beforeend',`<div data-test-track='may'><button aria-label='Воспроизведение'>Play</button><a href='/album/1/track/4'>1 мая</a><a href='/artist/3'>madk1d</a><a href='/artist/5'>CUPSIZE</a></div>`)");
            foreach(var spoken in new[]{"первого мая","первое мая","первый май","1-го мая"})
            {
                Check(await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query="madk1d "+spoken}),"Spoken date selects numeric track: "+spoken);
                Check(await page.Locator("[data-liquid-control='search']").EvaluateAsync<bool>("el=>el.parentElement.dataset.testTrack==='may'"),"Selected correct artist and date");
            }
            Check(!await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query="madk1d второго мая"}),"Wrong date must not select first result");
            await page.EvaluateAsync("()=>document.querySelector('[data-test-track=may] button').remove()");
            Check(await page.EvaluateAsync<bool>(script,new{action="search_match",service="yandex",query="madk1d первого мая"}),"Track match is distinct from button availability");
            Check(!await page.EvaluateAsync<bool>(script,new{action="search_target",service="yandex",query="madk1d первого мая"}),"Missing button is reported separately");
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
            await page.EvaluateAsync("()=>{const preview=document.createElement('video');preview.muted=true;Object.defineProperty(preview,'duration',{value:10});document.body.append(preview);}");
            state=await page.EvaluateAsync<JsonElement>(script,new{action="snapshot",service="yandex"});
            Check(state.GetProperty("playing").GetBoolean(),"Paused preview media must not override the main player's Pause button");
            Check(state.GetProperty("duration").GetDouble()!=10,"Preview duration must not replace the track duration");
            await page.EvaluateAsync("()=>document.querySelector('.PlayerBar_root button').setAttribute('aria-label','Воспроизведение')");
            state=await page.EvaluateAsync<JsonElement>(script,new{action="snapshot",service="yandex"});
            Check(!state.GetProperty("playing").GetBoolean(),"Main player's Play button reports paused playback");
            await page.EvaluateAsync("()=>document.querySelector('.PlayerBar_root button').setAttribute('aria-label','Очередь воспроизведения')");
            var queueTarget=await page.EvaluateAsync<JsonElement>(script,new{action="target",command="play",service="yandex"});
            Check(!queueTarget.GetProperty("found").GetBoolean(),"Playback queue is not a Play button");
            var foreground=Native.GetForegroundWindow();
            await page.EvaluateAsync("()=>document.title='background test'");
            Check(Native.GetForegroundWindow()==foreground,"Background browser command changed foreground");
            passed.Add("Background browser evaluation preserves Windows foreground");
            // Regression: DOM .click() is untrusted and can leave the selected
            // track paused. Exercise the same search path as a voice command.
            using var audioBytes=new MemoryStream();
            using(var wave=new BinaryWriter(audioBytes,System.Text.Encoding.UTF8,true))
            {
                wave.Write("RIFF"u8);wave.Write(32036);wave.Write("WAVEfmt "u8);wave.Write(16);
                wave.Write((short)1);wave.Write((short)1);wave.Write(16000);wave.Write(32000);wave.Write((short)2);wave.Write((short)16);
                wave.Write("data"u8);wave.Write(32000);wave.Write(new byte[32000]);
            }
            var playbackFixture="<meta charset='utf-8'><button role='tab' aria-selected='true'>Треки</button>"+
                "<style>[class*=EntityCard_root] button{opacity:0;pointer-events:none}[class*=EntityCard_root]:hover button{opacity:1;pointer-events:auto}</style>"+
                "<audio id='test-audio' loop src='data:audio/wav;base64,"+Convert.ToBase64String(audioBytes.ToArray())+"'></audio>"+
                "<div class='PlayerBar_root'><span class='Meta_title'>Старый трек</span><span class='Meta_artists'>VILLIAN, madk1d</span><button id='resume' aria-label='Воспроизвести'>Play</button></div>"+cards+"""
                <script>
                window.trustedSearch=false;window.resumeClicks=0;window.searchClicks=0;
                document.addEventListener('click',event=>{
                  const button=event.target.closest('button');if(!button)return;
                  const row=button.closest('[class*=EntityCard_root]');
                  if(row?.querySelector('a[href*="/track/"]')&&button.getAttribute('aria-label')==='Ойнату'){
                    if(!event.isTrusted)return;
                    window.searchClicks++;if(window.ignoreFirstClick&&window.searchClicks===1)return;
                    window.trustedSearch=true;document.querySelector('.Meta_title').textContent='Династия';
                    if(!window.deferPlayback){document.querySelector('#test-audio').play();document.querySelector('#resume').setAttribute('aria-label','Пауза');}
                  }
                  if(button.id==='resume'&&event.isTrusted){window.resumeClicks++;document.querySelector('#test-audio').play();button.setAttribute('aria-label','Пауза');}
                });
                </script>
                """;
            await page.UnrouteAllAsync();
            var deferPlayback=false;
            await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=(deferPlayback?"<script>window.deferPlayback=true</script>":"")+playbackFixture}));
            await BrowserController.SearchYandexAsync(page,"Династия",script);
            await page.WaitForFunctionAsync("()=>window.trustedSearch&&!document.querySelector('#test-audio').paused");
            state=await page.EvaluateAsync<System.Text.Json.JsonElement>(script,new{action="snapshot",service="yandex"});
            Check(state.GetProperty("playing").GetBoolean()&&state.GetProperty("title").GetString()=="Династия","Trusted search input starts the requested track");
            await page.WaitForFunctionAsync("()=>document.querySelector('#test-audio').currentTime>0");
            await page.EvaluateAsync("()=>{const blocker=document.createElement('div');blocker.id='test-blocker';blocker.style.cssText='position:fixed;inset:0;z-index:999999';document.body.append(blocker);}");
            await page.EvaluateAsync<JsonElement>(script,new{action="target",command="pause",service="yandex"});
            Check(!await BrowserController.ClickControlAsync(page,"target"),"Overlaid controls must not receive an accidental click");
            await page.EvaluateAsync("()=>document.querySelector('#test-blocker').remove()");
            deferPlayback=true;
            await BrowserController.SearchYandexAsync(page,"Династия",script);
            Check(await page.EvaluateAsync<bool>("()=>window.trustedSearch&&document.querySelector('#test-audio').paused"),"Regression fixture loads requested track on pause");
            Check(await BrowserController.ResumeYandexAsync(page,script),"Resume paused search result through the player");
            await page.WaitForFunctionAsync("()=>!document.querySelector('#test-audio').paused");
            Check(!await BrowserController.ResumeYandexAsync(page,script)&&await page.EvaluateAsync<int>("()=>window.resumeClicks")==1,"Playing track is not toggled back to pause");
            deferPlayback=false;
            await page.UnrouteAllAsync();
            await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body="<script>window.ignoreFirstClick=true</script>"+playbackFixture}));
            await BrowserController.SearchYandexAsync(page,"Династия",script);
            Check(await page.EvaluateAsync<bool>("()=>window.searchClicks===2&&!document.querySelector('#test-audio').paused"),"Retry a visible card whose first click had no effect");
            passed.Add("Trusted background input, ignored-first-click retry, paused-track resume, overlay rejection and native media playback");
            var waveFixture=File.ReadAllText(Path.Combine(AppPaths.Root,"tests","fixtures","yandex-wave.html"));
            await page.UnrouteAllAsync();
            await page.RouteAsync("**/*",route=>route.FulfillAsync(new(){ContentType="text/html; charset=utf-8",Body=waveFixture}));
            foreach(var domain in new[]{"music.yandex.ru","music.yandex.kz","music.yandex.com"})
            {
                await page.GotoAsync("https://"+domain+"/search?text=old");
                var waves=await BrowserController.WaveOptionsAsync(page,script);
                Check(waves.GetArrayLength()==4,"Only visible wave cards, including unavailable cards: "+domain);
                Check(waves[0].GetProperty("title").GetString()=="Это поп!","Nested wave card is not duplicated");
                Check(waves[3].GetProperty("disabled").GetBoolean(),"Unavailable wave stays disabled");
                Check(await page.EvaluateAsync<int>("()=>window.homeClicks")==1,"Uses existing My Wave navigation");
                var waveTarget=await page.EvaluateAsync<JsonElement>(script,new{action="wave_target",service="yandex",query="Русский рок"});
                Check(waveTarget.GetProperty("found").GetBoolean()&&await BrowserController.ClickControlAsync(page,"wave"),"Select wave with trusted input");
                Check(await page.EvaluateAsync<bool>("()=>window.waveClicks.join(',')==='rock'&&document.querySelector('.Meta_title').textContent==='Текущий трек'"),"Click reaches wave control and keeps player alive");
                waveTarget=await page.EvaluateAsync<JsonElement>(script,new{action="wave_target",service="yandex",query="Посторонняя подборка"});
                Check(!waveTarget.GetProperty("found").GetBoolean(),"Unrelated content cannot be selected as a wave");
                waveTarget=await page.EvaluateAsync<JsonElement>(script,new{action="wave_target",service="yandex",query="Недоступная волна"});
                Check(!waveTarget.GetProperty("found").GetBoolean(),"Disabled wave cannot be selected");
            }
            await page.EvaluateAsync("""
                ()=>{
                  const button=document.querySelector('[data-wave=rock]');
                  button.style.cssText='position:fixed;left:30px;top:180px;width:240px;height:80px';
                  const cover=document.createElement('div');cover.id='partial-cover';
                  cover.style.cssText='position:fixed;left:130px;top:200px;width:40px;height:40px;background:red;z-index:20';
                  document.body.append(cover);window.waveClicks=[];
                }
                """);
            Check(await BrowserController.ClickWaveAsync(page,script,"Русский рок"),"Click exposed part of wave when its centre is covered");
            Check(await page.EvaluateAsync<bool>("()=>window.waveClicks.join(',')==='rock'"),"Partially covered wave receives trusted click");
            await page.EvaluateAsync("""
                ()=>{
                  document.getElementById('partial-cover').remove();
                  const card=document.createElement('div');card.className='VibeWheel_item';
                  card.style.cssText='position:fixed;left:30px;top:300px;width:240px;height:80px';
                  card.innerHTML='<span>Саундскейп</span><button data-wave="sound" style="visibility:hidden;position:absolute;right:10px;top:10px;width:60px;height:50px" aria-label="Слушать"></button>';
                  card.addEventListener('pointerenter',()=>setTimeout(()=>{card.querySelector('button').style.visibility='visible';},180),{once:true});
                  document.querySelector('.VibeBlock_root').append(card);window.waveClicks=[];
                }
                """);
            Check(await BrowserController.ClickWaveAsync(page,script,"Саундскейп"),"Hover whole card and retry delayed control");
            Check(await page.EvaluateAsync<bool>("()=>window.waveClicks.join(',')==='sound'"),"Hover-only wave receives trusted click");
            await page.EvaluateAsync("""
                ()=>{
                  const dialog=document.createElement('div');dialog.style.cssText='position:fixed;inset:0;z-index:100;background:black';
                  document.body.append(dialog);window.waveClicks=[];
                }
                """);
            Check(!await BrowserController.ClickWaveAsync(page,script,"Русский рок"),"Real overlay still prevents clicking a wave");
            Check(await page.EvaluateAsync<int>("()=>window.waveClicks.length")==0,"Never click through an actual overlay");
            passed.Add("Dynamic My Wave cards, regional navigation, clipped/hover controls and real overlay rejection");
            Write("browser-selftest.json",new{ok=true,passed});return 0;
        }
        catch(Exception error){AppPaths.Log(error);Write("browser-selftest.json",new{ok=false,passed,error=error.ToString()});return 1;}
    }
    private static void Write(string file,object value)=>File.WriteAllText(Path.Combine(AppPaths.Logs,file),System.Text.Json.JsonSerializer.Serialize(value));
    public static async Task<int> LiveAsync(string query="Династия")
    {
        var foreground=Native.GetForegroundWindow();
        static string App(nint window){Native.GetWindowThreadProcessId(window,out var id);try{return System.Diagnostics.Process.GetProcessById((int)id).ProcessName;}catch{return "unknown";}}
        try
        {
            await using var controller=new BrowserController();
            var result=await controller.CommandAsync("yandex","search_play",query);
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
