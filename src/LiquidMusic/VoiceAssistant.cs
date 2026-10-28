using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NAudio.Wave;
using Vosk;

namespace LiquidMusic;

internal readonly record struct VoiceCommand(string Action, string? Query,double? Volume=null,string? Reply=null);
internal static class VoiceParser
{
    public static VoiceCommand Parse(string text)
    {
        text=Regex.Replace(text.Trim(' ','.',',','!','?','«','»','"'),@"\s+"," ");
        text=Regex.Replace(text,@"^пожалуйста[, ]+|[, ]+пожалуйста$","",RegexOptions.IgnoreCase);
        var action=text.ToLowerInvariant().Replace('ё','е') switch
        {
            "пауза" or "на паузу" or "поставь на паузу" => "pause",
            "стоп" or "останови музыку" => "stop",
            "продолжи" or "продолжить" or "продолжи музыку" or "включи музыку" => "play",
            "следующий" or "следующий трек" or "переключи трек" => "next",
            "предыдущий" or "предыдущий трек" => "previous",
            "лайк" or "поставь лайк" => "like",
            _ => ""
        };
        if(action.Length>0)return new(action,null);
        var match=Regex.Match(text,@"^(?:включи|включить|поставь|сыграй|найди)\s+(?:(?:трек|песню|композицию)\s+)?(.+)$",RegexOptions.IgnoreCase);
        if(match.Success)
        {
            var query=match.Groups[1].Value.Trim(' ','.',',','!','?','«','»','"');
            if(query.Length is >0 and <=200 && query is not ("трек" or "песню" or "композицию"))return new("search_play",query);
        }
        throw new ArgumentException("Скажите «включи трек Династия», «пауза» или «следующий трек»");
    }
}

internal sealed class VoiceAssistant(Func<VoiceCommand,Task> execute,Func<CancellationToken,Task<string>>? recognitionOverride=null,Func<VoiceCue,Task>? cueOverride=null,Func<string,CancellationToken,Task<VoiceCommand>>? resolve=null) : IDisposable
{
    private readonly object sync=new();
    private JsonObject state=new(){["phase"]="idle",["message"]="Скажите: «включи трек Династия»",["text"]=""};
    private CancellationTokenSource? cancellation;
    private int running;
    private Model? model;
    public bool Busy => Volatile.Read(ref running)!=0;
    public event Action? Changed;
    public JsonObject Snapshot(){lock(sync)return (JsonObject)state.DeepClone();}
    private void Set(string phase,string message,string? text=null)
    {
        lock(sync){state["phase"]=phase;state["message"]=message;if(text!=null)state["text"]=text;}
        Changed?.Invoke();
    }
    public void Start()
    {
        if(Interlocked.CompareExchange(ref running,1,0)!=0)return;
        cancellation=new();
        var token=cancellation.Token;
        Set("preparing","Подготовка микрофона…","");
        _=Task.Run(async()=>
        {
            try
            {
                string text;
                if(recognitionOverride!=null)text=await recognitionOverride(token);
                else
                {
                    if(!Directory.Exists(AppPaths.Model))throw new InvalidOperationException("Не найдена русская модель речи. Восстановите папку models рядом с приложением.");
                    Vosk.Vosk.SetLogLevel(-1);
                    model ??= new Model(AppPaths.Model);
                    token.ThrowIfCancellationRequested();
                    text=await RecognizeAsync(model,token);
                }
                token.ThrowIfCancellationRequested();
                Set("recognizing","Распознаю команду…",text);
                var command=resolve!=null?await resolve(text,token):VoiceParser.Parse(text);
                token.ThrowIfCancellationRequested();
                Set("waiting",command.Query!=null?"Ищу и включаю: "+command.Query:"Выполняю команду…");
                await (cueOverride ?? VoiceSounds.PlayAsync)(VoiceCue.Accepted);
                if(command.Action!="reply")await execute(command);
                Set("done",command.Reply??"Команда выполнена");
            }
            catch(OperationCanceledException){Set("idle","Запись отменена");}
            catch(Exception error){AppPaths.Log(error);Set("error",error.Message);}
            finally{Interlocked.Exchange(ref running,0);Changed?.Invoke();}
        });
    }
    public void Cancel()
    {
        if(Snapshot()["phase"]?.GetValue<string>()=="waiting")return;
        cancellation?.Cancel();
    }
    private async Task<string> RecognizeAsync(Model loaded,CancellationToken token)
    {
        var channel=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
        using var recognizer=new VoskRecognizer(loaded,16000);
        using var microphone=new WaveIn { DeviceNumber=-1,WaveFormat=new WaveFormat(16000,16,1),BufferMilliseconds=40,NumberOfBuffers=3 };
        var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        microphone.DataAvailable+=(_,e)=>channel.Writer.TryWrite(e.Buffer.AsSpan(0,e.BytesRecorded).ToArray());
        microphone.RecordingStopped+=(_,e)=>{if(e.Exception!=null)channel.Writer.TryComplete(e.Exception);else channel.Writer.TryComplete();stopped.TrySetResult();};
        var clock=Stopwatch.StartNew();
        var text="";
        var partial="";
        try
        {
            await (cueOverride ?? VoiceSounds.PlayAsync)(VoiceCue.Listening);
            token.ThrowIfCancellationRequested();
            microphone.StartRecording();
            clock.Restart();
            Set("listening","Слушаю…","");
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(9));
            try
            {
                await foreach(var data in channel.Reader.ReadAllAsync(limit.Token))
                {
                    if(recognizer.AcceptWaveform(data,data.Length))
                    {
                        text=JsonNode.Parse(recognizer.Result())?["text"]?.GetValue<string>()??"";
                        if(text.Length>0)break;
                    }
                    partial=JsonNode.Parse(recognizer.PartialResult())?["partial"]?.GetValue<string>()??"";
                    if(clock.Elapsed.TotalSeconds>5 && partial.Length==0)break;
                }
            }
            catch(OperationCanceledException) when(!token.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();
            if(text.Length==0)text=JsonNode.Parse(recognizer.FinalResult())?["text"]?.GetValue<string>()??"";
            if(text.Length==0)throw new InvalidOperationException("Не услышал команду. Проверьте микрофон по умолчанию и повторите.");
            return text;
        }
        finally
        {
            microphone.StopRecording();
            await Task.WhenAny(stopped.Task,Task.Delay(1500));
        }
    }
    public void Dispose(){cancellation?.Cancel(); /* Model remains alive until the microphone worker releases it. */}
}
