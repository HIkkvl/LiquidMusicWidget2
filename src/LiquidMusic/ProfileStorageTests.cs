namespace LiquidMusic;

internal static class ProfileStorageTests
{
    internal static void Run()
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        var root=Path.Combine(AppPaths.Data,"profile-storage-tests",Guid.NewGuid().ToString("N"));
        var oldInstall=Path.Combine(root,"old-build");var newInstall=Path.Combine(root,"new-build");
        var shared=Path.Combine(root,"shared");
        foreach(var name in new[]{"music-webview","spotify-edge"})
        {
            var legacy=Path.Combine(oldInstall,".native-data",name);Directory.CreateDirectory(Path.Combine(legacy,"Default","Network"));
            File.WriteAllText(Path.Combine(legacy,"Local State"),"fixture encryption metadata");
            File.WriteAllText(Path.Combine(legacy,"Default","Network","Cookies"),"fixture session");
            Directory.CreateDirectory(Path.Combine(legacy,"Cache"));File.WriteAllText(Path.Combine(legacy,"Cache","ignore"),"cache");
            File.WriteAllText(Path.Combine(legacy,"LOCK"),"stale lock");
            var migrated=ProfileStorage.Prepare(name,Path.Combine(shared,"Profiles"),new[]{newInstall,oldInstall});
            Check(File.ReadAllText(Path.Combine(migrated,"Default","Network","Cookies"))=="fixture session","Import app-owned login profile");
            Check(File.Exists(Path.Combine(migrated,"Local State")),"Keep encryption metadata with cookies");
            Check(!Directory.Exists(Path.Combine(migrated,"Cache"))&&!File.Exists(Path.Combine(migrated,"LOCK")),"Skip disposable cache and locks");
            File.WriteAllText(Path.Combine(migrated,"Default","Network","Cookies"),"updated session");
            var restarted=ProfileStorage.Prepare(name,Path.Combine(shared,"Profiles"),new[]{newInstall});
            Check(restarted==migrated&&File.ReadAllText(Path.Combine(restarted,"Default","Network","Cookies"))=="updated session","Another build reuses existing sessions without overwriting");
        }
        Directory.CreateDirectory(newInstall);File.WriteAllText(Path.Combine(newInstall,"settings.json"),"{\"yandex_domain\":\"music.yandex.kz\"}");
        var settings=ProfileStorage.PrepareSettings(shared,new[]{newInstall});
        Check(File.ReadAllText(settings).Contains("music.yandex.kz"),"Import account domain settings");
        File.WriteAllText(settings,"{\"service\":\"spotify\"}");
        Check(File.ReadAllText(ProfileStorage.PrepareSettings(shared,new[]{oldInstall}))=="{\"service\":\"spotify\"}","Shared account settings survive changing build directory");
        try{ProfileStorage.Prepare("../outside",shared,new[]{oldInstall});throw new InvalidOperationException("Invalid profile accepted");}catch(ArgumentException){}
    }
}
