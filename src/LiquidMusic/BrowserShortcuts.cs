using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiquidMusic;

internal static class BrowserShortcuts
{
    internal static string WithControlArguments(string original,int port)
    {
        if(port is <=0 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        var clean=Regex.Replace(original,@"(?:^|\s)--remote-debugging-(?:address|port)(?:=(?:""[^""]*""|\S+)|\s+\S+)?","",RegexOptions.IgnoreCase).Trim();
        return (clean+" --remote-debugging-address=127.0.0.1 --remote-debugging-port="+port).Trim();
    }
    public static bool Update(DefaultBrowserInfo info,int port,string? userDataDir=null)
    {
        bool result=false;Exception? error=null;
        var worker=new Thread(()=>{try{result=UpdateCore(info,port,userDataDir);}catch(Exception e){error=e;}}){IsBackground=true};
        worker.SetApartmentState(ApartmentState.STA);worker.Start();worker.Join();
        if(error!=null)throw new InvalidOperationException("Браузер подключён, но не удалось сохранить его ярлыки: "+error.Message,error);
        return result;
    }
    private static bool UpdateCore(DefaultBrowserInfo info,int port,string? userDataDir)
    {
        if(userDataDir!=null)return UpdateMusicShortcut(info,port,userDataDir);
        var backupRoot=Path.Combine(AppPaths.Data,"browser-shortcut-backups");Directory.CreateDirectory(backupRoot);
        var manifestFile=Path.Combine(backupRoot,"manifest.json");
        var backups=File.Exists(manifestFile)?JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(manifestFile))??new():new Dictionary<string,string>();
        var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs=Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var taskbar=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        foreach(var folder in new[]{desktop,taskbar})if(Directory.Exists(folder))foreach(var path in Directory.EnumerateFiles(folder,"*.lnk"))files.Add(path);
        if(Directory.Exists(programs))foreach(var path in Directory.EnumerateFiles(programs,"*.lnk",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint,MaxRecursionDepth=8}))files.Add(path);
        var shellType=Type.GetTypeFromProgID("WScript.Shell")??throw new InvalidOperationException("Недоступно создание ярлыков Windows");
        dynamic shell=Activator.CreateInstance(shellType)!;
        var updated=0;
        try
        {
            foreach(var path in files)
            {
                dynamic? shortcut=null;
                try
                {
                    shortcut=shell.CreateShortcut(path);
                    string target=shortcut.TargetPath;string arguments=shortcut.Arguments;
                    if(string.IsNullOrWhiteSpace(target)||!BrowserSetup.SameExecutable(target,info.Executable)||Regex.IsMatch(arguments,@"(?:^|\s)--app(?:-id)?=",RegexOptions.IgnoreCase))continue;
                    var next=WithControlArguments(arguments,port);
                    if(next==arguments){updated++;continue;}
                    if(!backups.ContainsKey(path))
                    {
                        var backup=Path.Combine(backupRoot,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))+".lnk");
                        if(!File.Exists(backup))File.Copy(path,backup,false);backups[path]=backup;
                        File.WriteAllText(manifestFile,JsonSerializer.Serialize(backups,new JsonSerializerOptions{WriteIndented=true}));
                    }
                    shortcut.Arguments=next;shortcut.Save();updated++;
                }
                catch(Exception e){AppPaths.Log("Browser shortcut: "+path+": "+e.Message);}
                finally{if(shortcut!=null)Marshal.FinalReleaseComObject(shortcut);}
            }
            // A per-user Start-menu shortcut also covers all-users installations
            // without editing protected/public shortcuts or Windows associations.
            Directory.CreateDirectory(programs);
            var canonical=Path.Combine(programs,info.Name+".lnk");
            if(!File.Exists(canonical))
            {
                dynamic shortcut=shell.CreateShortcut(canonical);
                try{shortcut.TargetPath=info.Executable;shortcut.WorkingDirectory=Path.GetDirectoryName(info.Executable);shortcut.IconLocation=info.Executable+",0";shortcut.Arguments=WithControlArguments("",port);shortcut.Save();updated++;}
                finally{Marshal.FinalReleaseComObject(shortcut);}
            }
        }
        finally{Marshal.FinalReleaseComObject(shell);}
        return updated>0;
    }
    private static bool UpdateMusicShortcut(DefaultBrowserInfo info,int port,string userDataDir)
    {
        var programs=Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programs);
        var shellType=Type.GetTypeFromProgID("WScript.Shell")??throw new InvalidOperationException("Недоступно создание ярлыков Windows");
        dynamic shell=Activator.CreateInstance(shellType)!;
        dynamic? shortcut=null;
        try
        {
            shortcut=shell.CreateShortcut(Path.Combine(programs,info.Name+" — Liquid Music.lnk"));
            shortcut.TargetPath=info.Executable;
            shortcut.WorkingDirectory=Path.GetDirectoryName(info.Executable);
            shortcut.IconLocation=info.Executable+",0";
            shortcut.Arguments=WithControlArguments("--user-data-dir=\""+userDataDir+"\" --restore-last-session",port);
            shortcut.Save();return true;
        }
        finally
        {
            if(shortcut!=null)Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
