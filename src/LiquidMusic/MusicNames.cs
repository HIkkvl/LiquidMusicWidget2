using System.Text;
using System.Text.RegularExpressions;

namespace LiquidMusic;

internal static class MusicNames
{
    public static string Normalize(string value)
    {
        const string letters="абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        string[] latin=["a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","shch","","y","","e","yu","ya"];
        var result=new StringBuilder();
        foreach(var character in value.ToLowerInvariant())
        {
            var index=letters.IndexOf(character);
            if(index<0)result.Append(character);else result.Append(latin[index]);
        }
        return Regex.Replace(result.ToString(),@"[^\p{L}\p{N}]+"," ").Trim();
    }
}
