using System.Text;
using System.Text.RegularExpressions;

namespace LiquidMusic;

internal static class MusicNames
{
    private static readonly Dictionary<string,string> Months=new()
    {
        ["январь"]="января",["января"]="января",["февраль"]="февраля",["февраля"]="февраля",["март"]="марта",["марта"]="марта",
        ["апрель"]="апреля",["апреля"]="апреля",["май"]="мая",["мая"]="мая",["июнь"]="июня",["июня"]="июня",["июль"]="июля",["июля"]="июля",
        ["август"]="августа",["августа"]="августа",["сентябрь"]="сентября",["сентября"]="сентября",["октябрь"]="октября",["октября"]="октября",
        ["ноябрь"]="ноября",["ноября"]="ноября",["декабрь"]="декабря",["декабря"]="декабря"
    };
    private static readonly Dictionary<string,int> Days=new()
    {
        ["один"]=1,["одно"]=1,["одного"]=1,["два"]=2,["три"]=3,["четыре"]=4,["пять"]=5,["шесть"]=6,["семь"]=7,["восемь"]=8,["девять"]=9,["десять"]=10,
        ["одиннадцать"]=11,["двенадцать"]=12,["тринадцать"]=13,["четырнадцать"]=14,["пятнадцать"]=15,["шестнадцать"]=16,["семнадцать"]=17,["восемнадцать"]=18,["девятнадцать"]=19,["двадцать"]=20,["тридцать"]=30
    };
    private static readonly Dictionary<string,int> Ordinals=new()
    {
        ["перв"]=1,["втор"]=2,["четверт"]=4,["пят"]=5,["шест"]=6,["седьм"]=7,["восьм"]=8,["девят"]=9,["десят"]=10,
        ["одиннадцат"]=11,["двенадцат"]=12,["тринадцат"]=13,["четырнадцат"]=14,["пятнадцат"]=15,["шестнадцат"]=16,["семнадцат"]=17,["восемнадцат"]=18,["девятнадцат"]=19,["двадцат"]=20,["тридцат"]=30
    };
    private static int Day(string word)
    {
        var numeric=Regex.Match(word,@"^(\d{1,2})(?:го|й|е|ое|ый|ого)?$");
        if(numeric.Success&&int.TryParse(numeric.Groups[1].Value,out var day)&&day is >=1 and <=31)return day;
        if(Days.TryGetValue(word,out day))return day;
        if(Regex.IsMatch(word,@"^трет(?:ий|ье|ьего|ьему|ьим|ьем|ья|ью|ьей)$"))return 3;
        var ordinal=Regex.Match(word,@"^(.+?)(?:ый|ой|ое|ого|ому|ым|ом|ая|ую)$");
        return ordinal.Success&&Ordinals.TryGetValue(ordinal.Groups[1].Value,out day)?day:0;
    }
    private static string NormalizeDates(string value)
    {
        var words=Regex.Replace(value.ToLowerInvariant().Replace('ё','е'),@"[^\p{L}\p{N}]+"," ").Split(' ',StringSplitOptions.RemoveEmptyEntries);
        for(var index=1;index<words.Length;index++)
        {
            if(!Months.TryGetValue(words[index],out var month))continue;
            var day=Day(words[index-1]);
            if(day==0&&index>=2&&words[index-1] is "го" or "й" or "е")
            {
                day=Day(words[index-2]);if(day>0)words[index-2]="";
            }
            if(day==0)continue;
            if(index>=2&&Day(words[index-2]) is 20 or 30&&day<10)
            {
                var combined=Day(words[index-2])+day;
                if(combined>31)continue;
                day=combined;words[index-2]="";
            }
            words[index-1]=day.ToString(System.Globalization.CultureInfo.InvariantCulture);words[index]=month;
        }
        return string.Join(" ",words);
    }
    public static string Normalize(string value)
    {
        const string letters="абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        string[] latin=["a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","shch","","y","","e","yu","ya"];
        var result=new StringBuilder();
        foreach(var character in NormalizeDates(value))
        {
            var index=letters.IndexOf(character);
            if(index<0)result.Append(character);else result.Append(latin[index]);
        }
        return Regex.Replace(result.ToString(),@"[^\p{L}\p{N}]+"," ").Trim();
    }
}
