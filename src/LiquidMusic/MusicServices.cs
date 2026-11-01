namespace LiquidMusic;

internal static class MusicServices
{
    internal static bool Supported(string? service)=>service is "yandex" or "youtube" or "spotify" or "soundcloud";
    internal static string Name(string service)=>service switch
    {"youtube"=>"YouTube Music","spotify"=>"Spotify","soundcloud"=>"SoundCloud",_=>"Яндекс Музыка"};
    internal static string Origin(string service)=>service switch
    {"youtube"=>"https://music.youtube.com/","spotify"=>"https://open.spotify.com/","soundcloud"=>"https://soundcloud.com/",_=>throw new ArgumentException("Укажите домен Яндекс Музыки.")};
}
