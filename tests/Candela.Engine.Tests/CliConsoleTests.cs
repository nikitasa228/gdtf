using System.Text;
using Candela.Cli;

namespace Candela.Engine.Tests;

/// <summary>
/// The command-line tool's UTF-8 console output on Windows (<see cref="Utf8ConsoleScope"/>): switched for the run and
/// the console window's code page restored afterwards, so the user's Command Prompt is not left in UTF-8.
/// </summary>
public sealed class CliConsoleTests
{
    [Fact]
    public void The_console_code_page_is_switched_for_the_run_and_restored_afterwards()
    {
        // Review regression: Console.OutputEncoding sets the code page of the console window (SetConsoleOutputCP), which
        // stayed UTF-8 in the user's Command Prompt after candela.exe ended.
        var oem = Encoding.Latin1; // stands for the OEM code page (866) of a Russian console
        Encoding current = oem;
        var changes = new List<int>();
        using (var scope = new Utf8ConsoleScope(() => current, e => { current = e; changes.Add(e.CodePage); }))
        {
            Assert.True(scope.Switched);
            Assert.Equal(65001, current.CodePage);
            Assert.Empty(current.GetPreamble()); // no byte-order mark in redirected output
        }

        Assert.Same(oem, current);
        Assert.Equal(new[] { 65001, oem.CodePage }, changes);
    }

    [Fact]
    public void A_utf8_console_is_left_alone_and_a_console_that_cannot_change_is_not_an_error()
    {
        int sets = 0;
        using (var scope = new Utf8ConsoleScope(() => new UTF8Encoding(false), _ => sets++))
            Assert.False(scope.Switched);
        Assert.Equal(0, sets);

        using (var scope = new Utf8ConsoleScope(() => Encoding.Latin1, _ => throw new IOException("The handle is invalid.")))
            Assert.False(scope.Switched);

        // the real console of this process (no change outside Windows)
        using var real = Utf8ConsoleScope.Enter();
        Assert.Equal(OperatingSystem.IsWindows() && Console.OutputEncoding.CodePage != 65001, real.Switched);
    }
}
