using System.Runtime.InteropServices;

namespace Candela.App.Ui;

/// <summary>
/// The UI language of a first start (no settings file yet): Russian when the operating system's display language is
/// Russian, otherwise English. Windows is asked for the user's UI language (<c>GetUserDefaultUILanguage</c>; the program
/// runs with invariant globalization, so .NET's cultures cannot tell), Linux and macOS for the locale variables
/// (<c>LC_ALL</c>, <c>LC_MESSAGES</c>, <c>LANG</c>). The choice is stored with the settings and changed in Settings →
/// Interface; <c>--language</c> overrides it for one run.
/// </summary>
internal static partial class SystemLanguage
{
    private const int LangRussian = 0x19;

    /// <summary>"ru" or "en" for this computer's display language. Never throws.</summary>
    public static string Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return FromWindowsLanguageId(GetUserDefaultUILanguage());
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return "en";
            }
        }

        return FromLocale(Environment.GetEnvironmentVariable("LC_ALL"), Environment.GetEnvironmentVariable("LC_MESSAGES"),
            Environment.GetEnvironmentVariable("LANG"));
    }

    /// <summary>"ru" for a Windows language id whose primary language is Russian (0x0419 and its variants), else "en".</summary>
    public static string FromWindowsLanguageId(int languageId) => (languageId & 0x3FF) == LangRussian ? "ru" : "en";

    /// <summary>
    /// "ru" when the first locale variable that is set (<c>LC_ALL</c>, then <c>LC_MESSAGES</c>, then <c>LANG</c>) names
    /// Russian ("ru_RU.UTF-8", "ru"), else "en".
    /// </summary>
    public static string FromLocale(string? lcAll, string? lcMessages, string? lang)
    {
        string? locale = new[] { lcAll, lcMessages, lang }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return locale is not null && (locale.Equals("ru", StringComparison.OrdinalIgnoreCase)
            || locale.StartsWith("ru_", StringComparison.OrdinalIgnoreCase) || locale.StartsWith("ru-", StringComparison.OrdinalIgnoreCase)
            || locale.StartsWith("ru.", StringComparison.OrdinalIgnoreCase))
            ? "ru"
            : "en";
    }

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
