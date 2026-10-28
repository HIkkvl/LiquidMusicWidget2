using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NAudio.Wave;
using Vosk;

namespace LiquidMusic;

internal readonly record struct VoiceCommand(string Action, string? Query,double? Volume=null,string? Reply=null);
internal sealed record VoiceRecording(string Text,byte[] Wave);
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
            "pause" or "pause music" => "pause",
            "stop" or "stop music" => "stop",
            "play" or "resume" or "resume music" => "play",
            "next" or "next track" or "next song" => "next",
            "previous" or "previous track" or "previous song" => "previous",
            "like" or "like this song" => "like",
            _ => ""
        };
        if(action.Length>0)return new(action,null);
        var match=Regex.Match(text,@"^(?:включи|включить|поставь|сыграй|найди|play|find)\s+(?:(?:трек|песню|композицию|song|track)\s+)?(.+)$",RegexOptions.IgnoreCase);
        if(match.Success)
        {
            var query=match.Groups[1].Value.Trim(' ','.',',','!','?','«','»','"');
            if(query.Length is >0 and <=200 && query is not ("трек" or "песню" or "композицию"))return new("search_play",query);
        }
        throw new ArgumentException("Скажите «включи трек Династия», «пауза» или «следующий трек»");
    }
}

internal sealed class VoiceAssistant(Func<VoiceCommand,Task> execute,Func<CancellationToken,Task<string>>? recognitionOverride=null,Func<VoiceCue,Task>? cueOverride=null,Func<string,CancellationToken,Task<VoiceCommand>>? resolve=null,Func<VoiceRecording,CancellationToken,Task<string>>? transcribe=null,Func<bool>? cloudRecognitionEnabled=null,Func<CancellationToken,Task<VoiceRecording>>? recordingOverride=null) : IDisposable
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
                var cloud=cloudRecognitionEnabled?.Invoke()==true&&transcribe!=null;
                VoiceRecording recording;
                if(recordingOverride!=null)recording=await recordingOverride(token);
                else if(recognitionOverride!=null)recording=new(await recognitionOverride(token),[]);
                else
                {
                    if(!Directory.Exists(AppPaths.Model))throw new InvalidOperationException("Не найдена русская модель речи. Восстановите папку models рядом с приложением.");
                    Vosk.Vosk.SetLogLevel(-1);
                    model ??= new Model(AppPaths.Model);
                    token.ThrowIfCancellationRequested();
                    recording=await RecognizeAsync(model,token,cloud);
                }
                token.ThrowIfCancellationRequested();
                var text=recording.Text;
                if(cloud&&recording.Wave.Length>44&&!IsQuickCommand(text))
                {
                    Set("recognizing","Распознаю русский и английский через Gemini…","");
                    text=await transcribe!(recording,token);
                }
                if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("Не удалось разобрать фразу. Повторите название и исполнителя.");
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
    private static bool IsQuickCommand(string text)
    {
        try{return DeepSeekAssistant.IsQuickCommand(VoiceParser.Parse(text));}catch(ArgumentException){return false;}
    }
    internal static byte[] ToWave(byte[] pcm)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write("RIFF"u8);writer.Write(pcm.Length+36);writer.Write("WAVEfmt "u8);
        writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(16000);writer.Write(32000);writer.Write((short)2);writer.Write((short)16);
        writer.Write("data"u8);writer.Write(pcm.Length);writer.Write(pcm);return stream.ToArray();
    }
    private async Task<VoiceRecording> RecognizeAsync(Model loaded,CancellationToken token,bool cloud)
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
        using var pcm=new MemoryStream();
        var heardSpeech=false;
        var lastSpeech=TimeSpan.Zero;
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
                    if(cloud)
                    {
                        pcm.Write(data);
                        double energy=0;for(var index=0;index+1<data.Length;index+=2){var sample=BitConverter.ToInt16(data,index);energy+=(double)sample*sample;}
                        if(data.Length>1&&Math.Sqrt(energy/(data.Length/2))>350){heardSpeech=true;lastSpeech=clock.Elapsed;}
                        if(heardSpeech&&clock.Elapsed-lastSpeech>TimeSpan.FromMilliseconds(1400))break;
                        if(!heardSpeech&&clock.Elapsed.TotalSeconds>5)break;
                    }
                    if(recognizer.AcceptWaveform(data,data.Length))
                    {
                        var segment=JsonNode.Parse(recognizer.Result())?["text"]?.GetValue<string>()??"";
                        text=(text+" "+segment).Trim();
                        if(text.Length>0&&!cloud)break;
                    }
                    partial=JsonNode.Parse(recognizer.PartialResult())?["partial"]?.GetValue<string>()??"";
                    if(!cloud&&clock.Elapsed.TotalSeconds>5 && partial.Length==0)break;
                }
            }
            catch(OperationCanceledException) when(!token.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();
            text=(text+" "+(JsonNode.Parse(recognizer.FinalResult())?["text"]?.GetValue<string>()??"")).Trim();
            if(text.Length==0&&(!cloud||!heardSpeech))throw new InvalidOperationException("Не услышал команду. Проверьте микрофон по умолчанию и повторите.");
            return new(text,cloud?ToWave(pcm.ToArray()):[]);
        }
        finally
        {
            microphone.StopRecording();
            await Task.WhenAny(stopped.Task,Task.Delay(1500));
        }
    }
    public void Dispose(){cancellation?.Cancel(); /* Model remains alive until the microphone worker releases it. */}
}
