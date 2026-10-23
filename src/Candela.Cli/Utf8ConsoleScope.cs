using System.Text;

namespace Candela.Cli;

/// <summary>
/// UTF-8 console output for one run of the tool on Windows, where the console's code page (437, 866, …) cannot show
/// every character the tool prints (file names in any script, ×, →, m²): UTF-8 shows them in the console and keeps them
/// intact in redirected output. The console's code page belongs to the console window, not to the process — it would
/// stay UTF-8 in the user's Command Prompt after the tool ended and garble older programs started there later — so
/// the previous code page is restored on <see cref="Dispose"/>. Elsewhere, or when the console cannot be changed,
/// nothing happens. Never throws.
/// </summary>
internal sealed class Utf8ConsoleScope : IDisposable
{
    private const int Utf8CodePage = 65001;
    private readonly Action<Encoding>? _restore;
    private readonly Encoding? _previous;

    /// <summary>
    /// Switches the output encoding with <paramref name="get"/> / <paramref name="set"/> (the console's, or a test's)
    /// to UTF-8 unless it already is UTF-8.
    /// </summary>
    internal Utf8ConsoleScope(Func<Encoding> get, Action<Encoding> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        try
        {
            var previous = get();
            if (previous.CodePage == Utf8CodePage)
                return;
            set(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _previous = previous;
            _restore = set;
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or System.Security.SecurityException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // No console or not allowed: the default encoding stays.
        }
    }

    /// <summary>True when the encoding was switched (and is restored on <see cref="Dispose"/>).</summary>
    internal bool Switched => _restore is not null;

    /// <summary>The scope for the process's console: switches on Windows only.</summary>
    public static Utf8ConsoleScope Enter() => OperatingSystem.IsWindows()
        ? new Utf8ConsoleScope(() => Console.OutputEncoding, encoding => Console.OutputEncoding = encoding)
        : new Utf8ConsoleScope(() => new UTF8Encoding(false), _ => { });

    /// <summary>Restores the previous output encoding (the console window's code page).</summary>
    public void Dispose()
    {
        if (_restore is null || _previous is null)
            return;
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
            _restore(_previous);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or System.Security.SecurityException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            // The console went away: nothing to restore.
        }
    }
}
