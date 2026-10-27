using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LiquidMusic;

internal static class BrowserConnectionTests
{
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
    public static async Task<int> RunAsync()
    {
        try
        {
            YandexMusicRegionTests.Run();
            var yandex=BrowserSetup.Identify(@"C:\Test\browser.exe");
            var original=new BrowserConnection(yandex.Executable,yandex.Name,54321,true,true);
            var attempts=new List<BrowserConnection>();
            var fallback=await BrowserSetup.LaunchWithFallbackAsync(yandex,original,true,(_,connection,background)=>
            {
                attempts.Add(connection);
                if(connection.UserDataDir==null)throw new TimeoutException("Default profile unavailable");
                Check(!background,"Fallback must show a window for account sign-in");
                return Task.CompletedTask;
            });
            Check(attempts.Count==2&&fallback.UserDataDir==Path.Combine(AppPaths.Data,"browser-profiles","yandex"),"Yandex profile fallback");
            Check(!fallback.ShortcutsUpdated&&fallback.Port==original.Port,"Fallback gets its own shortcut and retains port");
            Check(JsonSerializer.Deserialize<BrowserConnection>(JsonSerializer.Serialize(fallback))==fallback,"Persistent profile roundtrip");
            var legacy=JsonSerializer.Deserialize<BrowserConnection>("{\"Executable\":\"browser.exe\",\"Name\":\"Yandex\",\"Port\":54321,\"Configured\":true}");
            Check(legacy?.UserDataDir==null,"Existing connection files remain compatible");
            attempts.Clear();
            var reused=await BrowserSetup.LaunchWithFallbackAsync(yandex,original,false,(_,connection,_)=>{attempts.Add(connection);return Task.CompletedTask;});
            Check(reused==original&&attempts.Count==1,"Working regular profile stays selected");
            foreach(var pair in new[]{(yandex,fallback),(BrowserSetup.Identify(@"C:\Test\chrome.exe"),original)})
            {
                attempts.Clear();
                try
                {
                    await BrowserSetup.LaunchWithFallbackAsync(pair.Item1,pair.Item2,false,(_,connection,_)=>{attempts.Add(connection);throw new TimeoutException("Unavailable");});
                    throw new InvalidOperationException("Timeout was swallowed");
                }
                catch(TimeoutException){Check(attempts.Count==1,"No fallback loop or changes to other browsers");}
            }
            try
            {
                await BrowserSetup.LaunchWithFallbackAsync(yandex,original,false,(_,_,_)=>throw new IOException("Launch failed"));
                throw new InvalidOperationException("Launch error was swallowed");
            }
            catch(IOException){}
            await CheckEndpointAsync("127.0.0.1",true);
            await CheckEndpointAsync("localhost",true);
            await CheckEndpointAsync("192.0.2.1",false);
            await CheckEndpointAsync("127.0.0.1",false,true);
            Console.WriteLine("PASS: Yandex ru/kz/com selection and persistence; profile fallback, legacy settings, reuse, error propagation and local endpoint validation");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);AppPaths.Log(error);return 1;}
    }
    private static async Task CheckEndpointAsync(string host,bool expected,bool wrongPort=false)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream=client.GetStream();
            using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
            while(!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))){}
            var body=JsonSerializer.Serialize(new{webSocketDebuggerUrl=$"ws://{host}:{(wrongPort?port+1:port)}/devtools/browser/test"});
            var response=Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response,deadline.Token);
        });
        var ready=await BrowserSetup.EndpointReadyAsync(port);
        await server;
        Check(ready==expected,$"Endpoint host={host}, wrongPort={wrongPort}");
    }
}
