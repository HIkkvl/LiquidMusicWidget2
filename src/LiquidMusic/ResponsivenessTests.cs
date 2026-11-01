using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiquidMusic;

internal static class ResponsivenessTests
{
    internal static int Run()
    {
        ApplicationConfiguration.Initialize();
        using var loop=new ApplicationContext();
        using var start=new System.Windows.Forms.Timer{Interval=50};
        var exitCode=1;
        start.Tick+=async(_,_)=>
        {
            start.Stop();
            try
            {
                var hookLatency=await CheckMouseHookAsync();
                var discordUpdates=await CheckDiscordAsync();
                File.WriteAllText(Path.Combine(AppPaths.Logs,"responsiveness-test.json"),JsonSerializer.Serialize(new{ok=true,hookLatencyMs=hookLatency,discordUpdates}));
                exitCode=0;
            }
            catch(Exception error)
            {
                File.WriteAllText(Path.Combine(AppPaths.Logs,"responsiveness-test.json"),JsonSerializer.Serialize(new{ok=false,error=error.ToString()}));
            }
            finally{loop.ExitThread();}
        };
        start.Start();Application.Run(loop);return exitCode;
    }

    private static async Task<double> CheckMouseHookAsync()
    {
        using var dispatcherControl=new Control();_=dispatcherControl.Handle;
        using var wheel=new WheelControl{Options=JsonNode.Parse("""{"enabled":true,"modifier":"ctrl","step":5}""")};
        await wheel.Ready.WaitAsync(TimeSpan.FromSeconds(3));
        using var started=new ManualResetEventSlim();
        var probe=Task.Run(async()=>
        {
            var clock=Stopwatch.StartNew();
            started.Set();
            for(var i=0;i<20;i++)await wheel.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(2));
            return clock.Elapsed.TotalMilliseconds;
        });
        // Deliberately stall the UI, as a heavy WebView or engine startup can.
        // No synthetic mouse/keyboard input is sent to the user's desktop.
        if(!started.Wait(TimeSpan.FromSeconds(2)))throw new Exception("Hook probe did not start.");
        Thread.Sleep(500);
        var latency=await probe;
        if(latency>=250)throw new Exception("Mouse hook waited for the UI thread: "+latency+" ms");
        return latency;
    }

    private static async Task<int> CheckDiscordAsync()
    {
        var prefix="liquid-presence-test-"+Guid.NewGuid().ToString("N")+"-";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token=timeout.Token;
        using var server=new NamedPipeServerStream(prefix+"0",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var frames=new ConcurrentQueue<JsonObject>();
        var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeDiscord=Task.Run(async()=>
        {
            await server.WaitForConnectionAsync(token);
            await ReadFrameAsync(server,token); // Handshake.
            await server.WriteAsync(DiscordPresence.EncodeFrame(1,"""{"evt":"READY","data":{"user":{"username":"test"}}}"""),token);
            try
            {
                while(!token.IsCancellationRequested)
                {
                    var frame=await ReadFrameAsync(server,token);
                    frames.Enqueue(frame);
                    if(frame["args"]?["activity"]==null)cleared.TrySetResult();else first.TrySetResult();
                    await server.WriteAsync(DiscordPresence.EncodeFrame(1,"""{"evt":null,"cmd":"SET_ACTIVITY"}"""),token);
                }
            }
            catch(Exception error) when(error is OperationCanceledException or EndOfStreamException or IOException){}
        },token);
        await using(var presence=new DiscordPresence(prefix))
        {
            presence.Configure(JsonNode.Parse("""{"enabled":true,"client_id":"123456789012345678","show_artwork":true}"""));
            presence.Update(JsonNode.Parse("""{"connected":true,"title":"Test Song","artist":"Artist","playing":true,"position":10,"duration":180,"service":"spotify"}""")!.AsObject());
            await first.Task.WaitAsync(token);
            // No fresh snapshot for longer than the send interval. Progress
            // must continue without resetting Discord's activity every 5 s.
            await Task.Delay(7200,token);
            if(frames.Count!=1)throw new Exception("Unchanged Discord activity was resent: "+frames.Count);
            presence.Configure(JsonNode.Parse("""{"enabled":false}"""));
            await cleared.Task.WaitAsync(token);
        }
        timeout.Cancel();await fakeDiscord;
        return frames.Count;
    }

    private static async Task<JsonObject> ReadFrameAsync(Stream stream,CancellationToken token)
    {
        var header=new byte[8];await stream.ReadExactlyAsync(header,token);
        var length=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if(length is <0 or >65536)throw new InvalidDataException("Bad test IPC frame.");
        var body=new byte[length];await stream.ReadExactlyAsync(body,token);
        return JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject();
    }
}
