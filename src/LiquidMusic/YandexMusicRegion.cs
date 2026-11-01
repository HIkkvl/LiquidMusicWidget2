namespace LiquidMusic;

internal sealed class YandexMusicRegion(Settings settings)
{
    internal static bool IsDomain(string? domain)=>domain is "music.yandex.ru" or "music.yandex.kz" or "music.yandex.com";
    public string Choice=>settings.Value["yandex_domain"]?.GetValue<string>() is string domain&&IsDomain(domain)?domain:"auto";
    public string Origin=>"https://"+(Choice!="auto"?Choice:
        settings.Value["yandex_detected_domain"]?.GetValue<string>() is string remembered&&IsDomain(remembered)?remembered:"music.yandex.ru")+"/";
    public bool Accepts(string url)=>BrowserController.MatchesService(url,"yandex")&&
        (Choice=="auto"||new Uri(url).Host==Choice);
    public void Remember(string url)
    {
        if(!BrowserController.MatchesService(url,"yandex"))return;
        var domain=new Uri(url).Host;
        if(settings.Value["yandex_detected_domain"]?.GetValue<string>()==domain)return;
        settings.Value["yandex_detected_domain"]=domain;
        settings.Save();
    }
    public void Select(string choice)
    {
        if(choice!="auto"&&!IsDomain(choice))throw new ArgumentException("Неизвестный домен Яндекс Музыки");
        settings.Value["yandex_domain"]=choice;
        settings.Save();
    }
}
