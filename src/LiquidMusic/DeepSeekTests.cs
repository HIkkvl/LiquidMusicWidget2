using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiquidMusic;

internal static class DeepSeekTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status=HttpStatusCode.OK;
        public string Intent="{\"action\":\"volume\",\"volume\":0.4}";
        public JsonObject? Request;
        public bool BalanceAvailable=true;
        public string[] Models=["deepseek-chat"];
        public readonly List<Uri> Endpoints=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;
            Endpoints.Add(request.RequestUri!);
            if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/models",StringComparison.Ordinal)?JsonSerializer.Serialize(new{data=Models.Select(id=>new{id})}):JsonSerializer.Serialize(new{is_available=BalanceAvailable}))};
            if(request.Content!=null)Request=JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject();
            if(request.RequestUri!.AbsolutePath.EndsWith(":generateContent",StringComparison.Ordinal))return new(Status){Content=new StringContent(JsonSerializer.Serialize(new{candidates=new[]{new{finishReason="STOP",content=new{parts=new[]{new{text="{\"text\":\"play madk1d Rehab\"}"}}}}}}),Encoding.UTF8,"application/json")};
            return new(Status){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=Intent}}}}),Encoding.UTF8,"application/json")};
        }
    }
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    public static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(AppPaths.Data);
        var testFile=Path.Combine(AppPaths.Data,"assistant-test-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            var settings=new Settings(testFile);
            settings.Value["deepseek_enabled"]=true;
            var handler=new Handler();using var http=new HttpClient(handler);
            using var assistant=new DeepSeekAssistant(settings,http,()=>"test-key");
            Check((await assistant.ResolveAsync("пауза",new(),default)).Action=="pause"&&handler.Calls==0,"Quick command stays offline");
            var command=await assistant.ResolveAsync("Слишком громко, сделай потише",new(){["volume"]=0.5,["artwork"]="private-image"},default);
            Check(command.Action=="volume"&&command.Volume==0.4,"Natural phrase maps to volume");
            Check(!handler.Request!.ToJsonString().Contains("private-image")&&!handler.Request.ToJsonString().Contains("test-key"),"Only allowlisted player context goes into prompt");
            handler.Intent="{\"action\":\"search_play\",\"query\":\"Династия VILLIAN\"}";
            command=await assistant.ResolveAsync("Поставь Династию",new(),default);
            Check(command.Action=="search_play"&&command.Query=="Династия VILLIAN","Search intent");
            handler.Intent="{\"action\":\"reply\",\"reply\":\"Какого исполнителя?\"}";
            command=await assistant.ResolveAsync("Нет, другого",new(),default);
            Check(command.Action=="reply"&&handler.Request!["messages"]!.AsArray().Count>=5,"Clarification and short history");
            assistant.Reset();
            await assistant.ResolveAsync("Что сейчас играет?",new(),default);
            Check(handler.Request!["messages"]!.AsArray().Count==2,"Reset clears conversation");
            foreach(var invalid in new[]{"{\"action\":\"shell\",\"query\":\"calc\"}","{\"action\":\"volume\",\"volume\":1.5}","{\"action\":\"search_play\"}","{\"action\":\"reply\"}","not-json"})
            {
                handler.Intent=invalid;
                try{await assistant.ResolveAsync("Сделай что-нибудь",new(),default);throw new Exception("Invalid response accepted");}
                catch(InvalidOperationException error){Check(error.Message.Contains("Действие не выполнено"),"Invalid model output rejected");}
            }
            handler.Status=HttpStatusCode.PaymentRequired;
            try{await assistant.ResolveAsync("Что играет?",new(),default);throw new Exception("API error accepted");}
            catch(InvalidOperationException error){Check(error.Message.Contains("балансе"),"Balance error explained");}
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{await assistant.ResolveAsync("Что играет?",new(),cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
            settings.Value["deepseek_enabled"]=false;
            Check((await assistant.ResolveAsync("следующий трек",new(),default)).Action=="next","Disabled mode retains local parser");
            await assistant.TestAsync();
            Check(settings.Value["deepseek_model"]?.GetValue<string>()=="deepseek-chat","Pick a model available to this API key");
            handler.BalanceAvailable=false;
            try{await assistant.TestAsync();throw new Exception("Empty balance accepted");}
            catch(InvalidOperationException error){Check(error.Message.Contains("API-балансе"),"Balance checked without inference charges");}
            handler.Status=HttpStatusCode.OK;handler.Models=["models/gemini-3.5-flash-lite"];
            handler.Intent="{\"action\":\"volume\",\"volume\":0.4}";
            assistant.SelectProvider("gemini");assistant.SetEnabled(true);
            handler.Endpoints.Clear();
            await assistant.TestAsync();
            Check(settings.Value["gemini_model"]?.GetValue<string>()=="gemini-3.5-flash-lite","Normalize Google models/ prefixes");
            Check(handler.Endpoints.Count==2&&handler.Endpoints.All(uri=>uri.Host=="generativelanguage.googleapis.com"),"Gemini key stays on Google endpoints; generation quota is tested");
            command=await assistant.ResolveAsync("Сделай потише",new(){["volume"]=0.5},default);
            Check(command.Volume==0.4&&handler.Request!["thinking"]==null&&handler.Request["reasoning_effort"]?.GetValue<string>()=="minimal","Gemini-compatible request");
            Check(handler.Request!["messages"]!.AsArray().Count==2,"Provider switch clears previous conversation");
            var wave=VoiceAssistant.ToWave(new byte[32000]);
            Check(BitConverter.ToInt32(wave,24)==16000&&BitConverter.ToInt32(wave,40)==32000,"Valid 16kHz PCM WAV in memory");
            var recording=new VoiceRecording("включи мэтт кид греха",wave);
            var beforeAudio=handler.Calls;
            try{await assistant.TranscribeAsync(recording,default);throw new Exception("Audio uploaded without opt-in");}
            catch(InvalidOperationException error){Check(error.Message.Contains("Включите")&&handler.Calls==beforeAudio,"Audio mode requires explicit opt-in");}
            assistant.SetAudioOptions(true,"madk1d (мэд кид)");
            Check(await assistant.TranscribeAsync(recording,default)=="play madk1d Rehab","Audio transcription preserves artist spelling");
            Check(handler.Request!["contents"]?[0]?["parts"]?[1]?["inlineData"]?["mimeType"]?.GetValue<string>()=="audio/wav","Native Gemini audio format");
            Check(!handler.Request.ToJsonString().Contains(recording.Text),"Corrupted local transcript does not bias audio recognition");
            Check(BrowserController.VoiceMatch(new(){["title"]="РЕХАБ",["artist"]="madk1d"},"madk1d Rehab"),"Latin title matches Cyrillic catalogue title");
            Check(!BrowserController.VoiceMatch(new(){["title"]="РЕХАБ",["artist"]="Другой артист"},"madk1d Rehab"),"Artist remains required when specified");
            Check(VoiceParser.Parse("next track").Action=="next"&&VoiceParser.Parse("play madk1d Rehab").Query=="madk1d Rehab","English command parsing");
            async Task CheckVoiceAudio(bool cloud,string local,bool shouldUpload)
            {
                var complete=new TaskCompletionSource<VoiceCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
                var uploads=0;
                using var voice=new VoiceAssistant(command=>{complete.TrySetResult(command);return Task.CompletedTask;},cueOverride:_=>Task.CompletedTask,
                    resolve:(text,_)=>Task.FromResult(VoiceParser.Parse(text)),transcribe:(_,_)=>{uploads++;return Task.FromResult("play madk1d Rehab");},
                    cloudRecognitionEnabled:()=>cloud,recordingOverride:_=>Task.FromResult(new VoiceRecording(local,wave)));
                voice.Start();var chosen=await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(uploads==(shouldUpload?1:0),"Audio upload routing respects mode and quick commands");
                if(shouldUpload)Check(chosen.Query=="madk1d Rehab","Corrected transcription reaches command execution");
            }
            await CheckVoiceAudio(true,recording.Text,true);
            await CheckVoiceAudio(true,"пауза",false);
            await CheckVoiceAudio(false,recording.Text,false);
            handler.Status=HttpStatusCode.TooManyRequests;
            try{await assistant.ResolveAsync("Что играет?",new(),default);throw new Exception("Quota error accepted");}
            catch(InvalidOperationException error){Check(error.Message.Contains("квота")&&error.Message.Contains("Gemini"),"Gemini quota error explained");}
            assistant.SelectProvider("deepseek");
            Check(!assistant.Enabled&&settings.Value["gemini_enabled"]?.GetValue<bool>()==true,"Provider preferences stay separate");
            try{assistant.SelectProvider("https://evil.example");throw new Exception("Unknown provider accepted");}catch(ArgumentException){}
            var answered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executed=false;
            using(var voice=new VoiceAssistant(_=>{executed=true;return Task.CompletedTask;},_=>Task.FromResult("Кто поёт?"),_=>Task.CompletedTask,(_,_)=>Task.FromResult(new VoiceCommand("reply",null,null,"VILLIAN"))))
            {
                voice.Changed+=()=>{if(voice.Snapshot()["phase"]?.GetValue<string>()=="done")answered.TrySetResult();};
                voice.Start();await answered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!executed&&voice.Snapshot()["message"]?.GetValue<string>()=="VILLIAN","Reply is displayed without issuing a browser command");
            }
            var resolving=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelledVoice=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using(var voice=new VoiceAssistant(_=>{executed=true;return Task.CompletedTask;},_=>Task.FromResult("Сделай тише"),_=>Task.CompletedTask,async(_,token)=>
            {
                resolving.TrySetResult();await Task.Delay(Timeout.Infinite,token);return new("volume",null,0.4);
            }))
            {
                voice.Changed+=()=>{if(voice.Snapshot()["message"]?.GetValue<string>()=="Запись отменена")cancelledVoice.TrySetResult();};
                voice.Start();await resolving.Task.WaitAsync(TimeSpan.FromSeconds(3));voice.Cancel();
                await cancelledVoice.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!executed,"Cancel during interpretation prevents execution");
            }
            Console.WriteLine("PASS: Gemini/DeepSeek routing, provider isolation, model discovery, context/history, output validation, quota failures, cancellation and local fallback");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(File.Exists(testFile))File.Delete(testFile);}
    }
    public static async Task<int> ConfigureAsync(string provider="deepseek")
    {
        try
        {
            Console.WriteLine("Waiting for API key on standard input (not echoed)...");
            var key="";
            if(Console.IsInputRedirected)key=Console.ReadLine()??"";
            else
            {
                var input=new StringBuilder();
                while(true)
                {
                    var pressed=Console.ReadKey(true);
                    if(pressed.Key==ConsoleKey.Enter)break;
                    if(pressed.Key==ConsoleKey.Backspace){if(input.Length>0)input.Length--;}
                    else if(!char.IsControl(pressed.KeyChar))input.Append(pressed.KeyChar);
                }
                key=input.ToString();
            }
            var settings=new Settings();using var assistant=new DeepSeekAssistant(settings);
            assistant.SelectProvider(provider);
            if(!string.IsNullOrWhiteSpace(key))assistant.SaveKey(key);
            else if(!assistant.HasKey)throw new InvalidOperationException("Enter an API key first.");
            await assistant.TestAsync();
            assistant.SetEnabled(true);
            Console.WriteLine(provider+" configured; encrypted key saved locally. Model: "+settings.Value[provider+"_model"]);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
    public static async Task<int> LiveAsync()
    {
        try
        {
            var settings=new Settings();using var assistant=new DeepSeekAssistant(settings);
            if(!assistant.Enabled)throw new InvalidOperationException("Configure the assistant first.");
            var intent=await assistant.ResolveAsync("Слишком громко, сделай чуть потише",new(){["connected"]=true,["volume"]=0.6,["title"]="Династия",["artist"]="VILLIAN"},default);
            Check(intent.Action=="volume"&&intent.Volume<0.6,"Live volume intent");
            Console.WriteLine("PASS live "+assistant.Provider+": "+intent.Action+" "+intent.Volume);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
    public static async Task<int> AudioAsync(string file)
    {
        try
        {
            var settings=new Settings();
            // Explicit CLI test on a supplied sample, not a saved preference.
            settings.Value["gemini_audio"]=true;
            using var assistant=new DeepSeekAssistant(settings);
            var text=await assistant.TranscribeAsync(new("",File.ReadAllBytes(file)),default);
            var command=await assistant.ResolveAsync(text,new(),default);
            Console.WriteLine("Synthetic audio transcript: "+text);
            Console.WriteLine("Resolved action: "+command.Action+"; query: "+command.Query);
            Check(command.Action=="search_play"&&MusicNames.Normalize(command.Query??"").Contains("madk1d")&&MusicNames.Normalize(command.Query??"").Contains("rehab"),"Expected madk1d Rehab from the English sample");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
