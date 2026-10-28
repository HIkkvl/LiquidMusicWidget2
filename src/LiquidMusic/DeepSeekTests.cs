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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;
            if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath=="/models"?"{\"data\":[{\"id\":\"deepseek-chat\"}]}":JsonSerializer.Serialize(new{is_available=BalanceAvailable}))};
            if(request.Content!=null)Request=JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject();
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
            Console.WriteLine("PASS: DeepSeek routing, context/history, clarification, output validation, API failures, cancellation and local fallback");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(File.Exists(testFile))File.Delete(testFile);}
    }
    public static async Task<int> ConfigureAsync()
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
            DeepSeekKey.Save(key);
            var settings=new Settings();using var assistant=new DeepSeekAssistant(settings);
            await assistant.TestAsync();
            settings.Value["deepseek_enabled"]=true;settings.Save();
            Console.WriteLine("DeepSeek configured; encrypted key saved locally. Model: "+settings.Value["deepseek_model"]);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
    public static async Task<int> LiveAsync()
    {
        try
        {
            var settings=new Settings();using var assistant=new DeepSeekAssistant(settings);
            if(!assistant.Enabled)throw new InvalidOperationException("Configure DeepSeek first.");
            var intent=await assistant.ResolveAsync("Слишком громко, сделай чуть потише",new(){["connected"]=true,["volume"]=0.6,["title"]="Династия",["artist"]="VILLIAN"},default);
            Check(intent.Action=="volume"&&intent.Volume<0.6,"Live volume intent");
            Console.WriteLine("PASS live DeepSeek: "+intent.Action+" "+intent.Volume);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
