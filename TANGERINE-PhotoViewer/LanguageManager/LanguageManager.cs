using System.Globalization;
using System.Reflection;
using System.Resources;

public static class LanguageManager
{
    private static readonly ResourceManager rm = new("TANGERINE_PhotoViewer.LanguageManager.UiStrings", Assembly.GetExecutingAssembly());

    public static string Get(string key) => rm.GetString(key, CultureInfo.CurrentUICulture)
        ?? rm.GetString(key, CultureInfo.GetCultureInfo("en-US")) ?? key;
}
