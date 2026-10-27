namespace LiquidMusic;

internal static class YandexMusicRegionTests
{
    public static void Run()
    {
        static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        Directory.CreateDirectory(AppPaths.Data);
        var file=Path.Combine(AppPaths.Data,"region-test-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            var settings=new Settings(file);
            var region=new YandexMusicRegion(settings);
            Check(region.Choice=="auto"&&region.Origin=="https://music.yandex.ru/","Fresh installations must not force kz");
            foreach(var domain in new[]{"music.yandex.ru","music.yandex.kz","music.yandex.com"})
            {
                var url="https://"+domain+"/search?text=test";
                Check(region.Accepts(url),"Auto accepts "+domain);
                region.Remember(url);
                Check(new YandexMusicRegion(new Settings(file)).Origin=="https://"+domain+"/","Remember regional domain after restart");
            }
            region.Remember("https://music.yandex.ru/");
            region.Select("music.yandex.kz");
            Check(new YandexMusicRegion(new Settings(file)).Choice=="music.yandex.kz","Explicit kz choice survives restart");
            Check(region.Origin=="https://music.yandex.kz/"&&!region.Accepts("https://music.yandex.ru/")&&region.Accepts("https://music.yandex.kz/"),"Explicit kz overrides remembered ru");
            region.Select("music.yandex.ru");
            Check(region.Origin=="https://music.yandex.ru/"&&!region.Accepts("https://music.yandex.kz/"),"Explicit ru overrides kz tabs");
            region.Select("auto");
            foreach(var invalid in new[]{"https://music.yandex.kz.evil.example/","http://music.yandex.kz/","https://yandex.kz/"})
            {
                Check(!region.Accepts(invalid),"Invalid regional URL rejected");
                region.Remember(invalid);
                Check(region.Origin=="https://music.yandex.ru/","Invalid URL must not replace saved domain");
            }
            try{region.Select("evil.example");throw new InvalidOperationException("Invalid setting accepted");}catch(ArgumentException){}
            Check(new YandexMusicRegion(new Settings(file)).Choice=="auto","Explicit choice persists");
            settings.Value["yandex_detected_domain"]="evil.example";
            settings.Value["yandex_domain"]="evil.example";
            Check(region.Origin=="https://music.yandex.ru/","Invalid stored hosts use safe default");
        }
        finally{if(File.Exists(file))File.Delete(file);}
    }
}
