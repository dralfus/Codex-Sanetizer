using System.Globalization;
using System.Resources;

namespace CodexRedactionGate;

internal static class AppStrings
{
    private static readonly ResourceManager ResourceManager = new(
        "CodexRedactionGate.AppStrings",
        typeof(AppStrings).Assembly);

    public static string Get(string key)
    {
        return ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }
}
