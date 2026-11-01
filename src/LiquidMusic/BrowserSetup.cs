using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LiquidMusic;

internal sealed record DefaultBrowserInfo(string Name,string Executable,string ProcessName,bool Supported,string Error="");
internal sealed record BrowserConnection(string Executable,string Name,int Port,bool Configured,bool ShortcutsUpdated=false,string? UserDataDir=null);

internal static class BrowserSetup
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    private static string ConnectionFile=>Path.Combine(AppPaths.Data,"browser-control.json");
    public static bool Configuring {get;private set;}
    public static DefaultBrowserInfo DetectDefault()
    {
        try
        {
            using var choice=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId=choice?.GetValue("ProgId") as string;
            // Without a per-user association, HTTPS itself contains the open
            // command; its default value is a description, not another ProgId.
            using var command=Registry.ClassesRoot.OpenSubKey((string.IsNullOrWhiteSpace(progId)?"https":progId)+@"\shell\open\command");
            var value=command?.GetValue("") as string;
            var executable=ParseExecutable(value??"");
            if(!File.Exists(executable))throw new InvalidOperationException("Не найден исполняемый файл браузера по умолчанию Windows.");
            return Identify(executable);
        }
        catch(Exception error){return new("Браузер Windows","","",false,error.Message);}
    }
    internal static string ParseExecutable(string command)
    {
        var match=Regex.Match(Environment.ExpandEnvironmentVariables(command),"^\\s*(?:\"(?<exe>[^\"]+\\.exe)\"|(?<exe>.*?\\.exe))(?:\\s|$)",RegexOptions.IgnoreCase);
        if(!match.Success)throw new ArgumentException("Не удалось прочитать команду запуска браузера Windows.");
        return Path.GetFullPath(match.Groups["exe"].Value);
    }
    internal static DefaultBrowserInfo Identify(string executable)
    {
        var process=Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        var name=process switch{"brave"=>"Brave","chrome"=>"Google Chrome","msedge"=>"Microsoft Edge","vivaldi"=>"Vivaldi","opera"=>"Opera","browser"=>"Яндекс Браузер","firefox"=>"Firefox",_=>Path.GetFileNameWithoutExtension(executable)};
        var supported=process is "brave" or "chrome" or "msedge" or "vivaldi" or "opera" or "browser";
        return new(name,executable,process,supported,supported?"":name+" не поддерживает используемый канал CDP. Выберите в Windows браузер на Chromium: Brave, Edge, Chrome, Opera, Vivaldi или Яндекс Браузер.");
    }
    internal static bool SameExecutable(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    public static BrowserConnection? Read(DefaultBrowserInfo info)
    {
        try
        {
            if(File.Exists(ConnectionFile))
            {
                var saved=JsonSerializer.Deserialize<BrowserConnection>(File.ReadAllText(ConnectionFile),JsonOptions);
                return saved is {Configured:true,Port:>0 and <65536}&&SameExecutable(saved.Executable,info.Executable)?saved:null;
            }
            // Keep an already authorised Brave connection after the app update.
            if(info.ProcessName=="brave")
            {
                var legacy=JsonNode.Parse(File.ReadAllText(Path.Combine(AppPaths.Data,"brave-control.json")));
                var port=legacy?["port"]?.GetValue<int>()??0;
                if(port is >0 and <65536)return new(info.Executable,info.Name,port,true);
            }
        }
        catch { }
        return null;
    }
    public static void Save(BrowserConnection connection)
    {
        Directory.CreateDirectory(AppPaths.Data);
        var temp=ConnectionFile+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(connection,JsonOptions));
        File.Move(temp,ConnectionFile,true);
    }
    private static bool Matches(Process process,DefaultBrowserInfo info)
    {
        try{return SameExecutable(process.MainModule?.FileName??"",info.Executable);}catch{return false;}
    }
    public static bool IsRunning(DefaultBrowserInfo info)=>Process.GetProcessesByName(info.ProcessName).Any(process=>Matches(process,info));
    internal static async Task<bool> EndpointReadyAsync(int port)
    {
        try
        {
            using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromMilliseconds(800)};
            var info=JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version"));
            var endpoint=info?["webSocketDebuggerUrl"]?.GetValue<string>();
            return Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&uri.Scheme=="ws"&&uri.Port==port&&
                (uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||IPAddress.TryParse(uri.Host,out var address)&&IPAddress.IsLoopback(address));
        }
        catch{return false;}
    }
    public static async Task<BrowserConnection> ConfigureAsync(bool restart,bool updateShortcuts)
    {
        if(Configuring)throw new InvalidOperationException("Подключение браузера уже выполняется.");
        Configuring=true;
        try
        {
            var info=DetectDefault();
            if(!info.Supported)throw new InvalidOperationException(info.Error);
            var saved=Read(info);
            if(saved!=null&&await EndpointReadyAsync(saved.Port))
            {
                if(updateShortcuts&&!saved.ShortcutsUpdated)saved=saved with{ShortcutsUpdated=BrowserShortcuts.Update(info,saved.Port,saved.UserDataDir)};
                Save(saved);return saved;
            }
            // An isolated music profile can start alongside the user's regular
            // browser. Do not close unrelated windows when restoring it.
            if(saved?.UserDataDir!=null)
            {
                await LaunchAsync(info,saved,!restart);
                if(updateShortcuts&&!saved.ShortcutsUpdated)saved=saved with{ShortcutsUpdated=BrowserShortcuts.Update(info,saved.Port,saved.UserDataDir)};
                Save(saved);return saved;
            }
            var processes=Process.GetProcessesByName(info.ProcessName).Where(process=>Matches(process,info)).ToArray();
            if(processes.Length>0&&!restart)throw new InvalidOperationException(info.Name+" уже открыт без канала управления. Закройте его или подключите в настройках.");
            foreach(var process in processes.Where(process=>process.MainWindowHandle!=0))
                if(!process.CloseMainWindow())throw new InvalidOperationException("Не удалось корректно закрыть "+info.Name+". Сохраните работу и закройте браузер вручную.");
            var deadline=DateTime.UtcNow.AddSeconds(25);
            while(IsRunning(info)&&DateTime.UtcNow<deadline)await Task.Delay(300);
            if(IsRunning(info))throw new InvalidOperationException(info.Name+" не завершился. Закройте его через меню «Выход» и повторите. Принудительное завершение отключено.");
            var port=saved?.Port??0;
            if(port<=0){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();}
            var connection=new BrowserConnection(info.Executable,info.Name,port,true,saved?.ShortcutsUpdated??false);
            connection=await LaunchWithFallbackAsync(info,connection,!restart,LaunchAsync);
            // Keep a working connection even if Windows rejects shortcut edits.
            Save(connection);
            if(updateShortcuts&&!connection.ShortcutsUpdated)connection=connection with{ShortcutsUpdated=BrowserShortcuts.Update(info,port,connection.UserDataDir)};
            Save(connection);return connection;
        }
        finally{Configuring=false;}
    }
    internal static async Task<BrowserConnection> LaunchWithFallbackAsync(DefaultBrowserInfo info,BrowserConnection connection,bool background,Func<DefaultBrowserInfo,BrowserConnection,bool,Task> launch)
    {
        try{await launch(info,connection,background);}
        catch(TimeoutException) when(info.ProcessName=="browser"&&connection.UserDataDir==null)
        {
            // New Chromium builds may reject CDP for their default profile.
            // Use an empty, persistent profile rather than copying accounts
            // or disabling browser security features.
            connection=connection with{UserDataDir=Path.Combine(AppPaths.Data,"browser-profiles","yandex"),ShortcutsUpdated=false};
            await launch(info,connection,false);
        }
        return connection;
    }
    public static async Task LaunchAsync(DefaultBrowserInfo info,BrowserConnection connection,bool background)
    {
        if(await EndpointReadyAsync(connection.Port))return;
        var start=new ProcessStartInfo(info.Executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Minimized};
        start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        start.ArgumentList.Add("--remote-debugging-port="+connection.Port);
        if(connection.UserDataDir!=null)
        {
            Directory.CreateDirectory(connection.UserDataDir);
            start.ArgumentList.Add("--user-data-dir="+connection.UserDataDir);
        }
        start.ArgumentList.Add("--restore-last-session");
        if(background)start.ArgumentList.Add("--no-startup-window");
        using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось запустить "+info.Name+".");
        var until=DateTime.UtcNow.AddSeconds(18);
        while(DateTime.UtcNow<until){if(await EndpointReadyAsync(connection.Port))return;await Task.Delay(300);}
        var profile=connection.UserDataDir==null?"обычного профиля":"отдельного музыкального профиля";
        throw new TimeoutException($"{info.Name}: канал управления {profile} на 127.0.0.1:{connection.Port} не ответил. Проверьте, что браузер запустился. Если удалённая отладка запрещена администратором, обратитесь к нему. Повторите подключение в настройках.");
    }
}
