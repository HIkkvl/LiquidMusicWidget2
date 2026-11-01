namespace LiquidMusic;

// Login belongs to the Windows user, not the unpacked release directory.
// Import only this application's own profiles, never desktop-browser data.
internal static class ProfileStorage
{
    internal static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiquidMusic");
    internal static string ProfilePath(string name)=>Path.Combine(Root,"Profiles",ValidateName(name));
    internal static string SettingsPath()=>PrepareSettings(Root,LegacyInstallations());
    internal static string Prepare(string name)=>Prepare(name,Path.Combine(Root,"Profiles"),LegacyInstallations());
    private static string ValidateName(string name)=>name is "music-webview" or "spotify-edge"?name:throw new ArgumentException("Неизвестный профиль плеера.",nameof(name));
    private static IEnumerable<string> LegacyInstallations()
    {
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase){AppPaths.Root,AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)};
        // Portable builds beside the current build are previous installations
        // only when they contain our executable and UI. Do not scan user data.
        var parents=new[]{Directory.GetParent(AppPaths.Root),Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))}
            .Where(parent=>parent!=null).Select(parent=>parent!.FullName).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach(var parent in parents)
        {
            foreach(var directory in new DirectoryInfo(parent).EnumerateDirectories())
                if(File.Exists(Path.Combine(directory.FullName,"LiquidMusic.exe"))&&File.Exists(Path.Combine(directory.FullName,"ui","index.html")))roots.Add(directory.FullName);
        }
        return roots;
    }
    internal static string PrepareSettings(string root,IEnumerable<string> installations)
    {
        Directory.CreateDirectory(root);
        var target=Path.Combine(root,"settings.json");
        if(File.Exists(target))return target;
        var source=installations.Select(path=>Path.Combine(path,"settings.json")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if(source!=null)File.Copy(source,target,false);
        return target;
    }
    internal static string Prepare(string name,string root,IEnumerable<string> installations)
    {
        var target=Path.Combine(root,ValidateName(name));
        if(Directory.Exists(target))return target;
        Directory.CreateDirectory(root);
        var source=installations.Select(path=>Path.Combine(path,".native-data",name)).Where(Directory.Exists)
            .OrderByDescending(ProfileUpdated).FirstOrDefault();
        if(source==null){Directory.CreateDirectory(target);return target;}
        var staging=Path.Combine(root,name+".migrate-"+Guid.NewGuid().ToString("N"));
        try
        {
            CopyProfile(new DirectoryInfo(source),new DirectoryInfo(staging));
            Directory.Move(staging,target);
            return target;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {
            if(Directory.Exists(staging))try{Directory.Delete(staging,true);}catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException){}
            throw new InvalidOperationException("Не удалось перенести сохранённый вход. Закройте предыдущую версию Liquid Music и запустите приложение снова.",error);
        }
    }
    private static DateTime ProfileUpdated(string path)
    {
        var cookieFiles=new[]{"Default/Network/Cookies","Default/Network/Cookies-wal","EBWebView/Default/Network/Cookies","EBWebView/Default/Network/Cookies-wal"};
        var dates=cookieFiles.Select(file=>Path.Combine(path,file.Replace('/',Path.DirectorySeparatorChar))).Where(File.Exists).Select(File.GetLastWriteTimeUtc).ToArray();
        return dates.Length>0?dates.Max():Directory.GetLastWriteTimeUtc(path);
    }
    private static void CopyProfile(DirectoryInfo source,DirectoryInfo target)
    {
        target.Create();
        foreach(var entry in source.EnumerateFileSystemInfos())
        {
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0)continue;
            if(entry.Name is "Cache" or "Code Cache" or "GPUCache" or "ShaderCache" or "GrShaderCache" or "DawnGraphiteCache" or "DawnWebGPUCache" or "Crashpad" or "CrashReports")continue;
            if(entry.Name.Equals("LOCK",StringComparison.OrdinalIgnoreCase)||entry.Name.Equals("lockfile",StringComparison.OrdinalIgnoreCase)||entry.Name.StartsWith("Singleton",StringComparison.OrdinalIgnoreCase)||entry.Name=="DevToolsActivePort")continue;
            var destination=Path.Combine(target.FullName,entry.Name);
            if(entry is DirectoryInfo directory)CopyProfile(directory,new DirectoryInfo(destination));
            else File.Copy(entry.FullName,destination,false);
        }
    }
}
