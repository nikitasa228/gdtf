using System.Diagnostics;
using System.Text;

namespace Candela.App.Tests;

/// <summary>
/// The Windows packaging helper (<c>packaging/windows/winpack.py stage</c>): the installed layout it completes and the
/// uninstaller's file list it generates for <c>installer.nsi</c>. Runs the real script with Python 3 on a small published
/// folder; skipped where no Python 3 is installed.
/// </summary>
public sealed class PackagingTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [SkippableFact]
    public void The_uninstall_list_names_every_staged_file_with_dollar_signs_escaped()
    {
        // Review regression: a '$' in a staged file or folder name went into the NSIS script unescaped, where '$name'
        // is a variable — the build failed or the uninstaller deleted a different path.
        string python = FindPython();
        string publish = Path.Combine(_dir.Path, "publish");
        string generated = Path.Combine(_dir.Path, "generated");
        Directory.CreateDirectory(Path.Combine(publish, "sub$dir"));
        File.WriteAllText(Path.Combine(publish, "candela-app.exe"), "stub");
        File.WriteAllText(Path.Combine(publish, "price$list.txt"), "stub");
        File.WriteAllText(Path.Combine(publish, "sub$dir", "a.txt"), "stub");

        var (code, output) = Run(python, "-I", Path.Combine(EngineTestSupport.Root, "packaging", "windows", "winpack.py"), "stage",
            EngineTestSupport.Root, publish, generated);
        Assert.True(code == 0, output);

        string[] lines = File.ReadAllLines(Path.Combine(generated, "uninstall-files.nsh"), Encoding.UTF8);
        Assert.Contains(@"Delete ""$INSTDIR\price$$list.txt""", lines);
        Assert.Contains(@"Delete ""$INSTDIR\sub$$dir\a.txt""", lines);
        Assert.Contains(@"RMDir ""$INSTDIR\sub$$dir""", lines);
        // The installed layout of InstallLayout, with the sample scene's Cyrillic name intact.
        Assert.Contains(@"Delete ""$INSTDIR\data\specs\fixtures_index.json""", lines);
        Assert.Contains(@"Delete ""$INSTDIR\samples\Фокина.mvr""", lines);
        Assert.Contains(@"Delete ""$INSTDIR\licenses\Inter-OFL.txt""", lines);

        // Exactly one Delete per staged file, and a '$' only as '$INSTDIR' or doubled.
        var staged = Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(staged.Count, lines.Count(l => l.StartsWith("Delete ", StringComparison.Ordinal)));
        foreach (var line in lines.Where(l => !l.StartsWith(';')))
        {
            string rest = line.Replace("$INSTDIR", "", StringComparison.Ordinal).Replace("$$", "", StringComparison.Ordinal);
            Assert.DoesNotContain("$", rest, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_font_licences_are_copied_into_the_licenses_folder()
    {
        // Review regression: LinkBase does not apply to files inside the project folder, so the licences went to
        // Assets/Fonts in the output (and into the installation twice) instead of licenses/ as docs/editor.md says.
        foreach (var name in new[] { "Inter-OFL.txt", "Lucide-LICENSE.txt" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "licenses", name)), name);
    }

    private static string FindPython()
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "py.exe" } : new[] { "python3" })
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        Skip.If(true, "Python 3 is not installed.");
        return "";
    }

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "winpack.py did not finish");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
