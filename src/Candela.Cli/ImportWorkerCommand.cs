using Candela.Engine;

namespace Candela.Cli;

/// <summary>
/// Hidden mode <c>candela import-worker &lt;input&gt; &lt;output-dir&gt; [options]</c>: imports one 3D model with
/// Candela.Assets in this (separate) process and writes Candela's own model package into the output folder for the
/// application to load (see <see cref="ImportWorker"/>). A crash of the native importer ends only this process.
/// Exit codes: 0 imported, 2 usage error, 4 the file cannot be imported (reason in <c>error.txt</c>).
/// </summary>
internal static class ImportWorkerCommand
{
    public static int Run(string[] args, TextWriter error)
    {
        if (args.Length < 2) throw new UsageException($"{ImportWorker.CommandName} needs <input> <output-dir>.");
        Candela.Assets.ImportOptions options;
        try
        {
            options = ImportWorker.ParseOptions(args[2..]);
        }
        catch (FormatException e)
        {
            throw new UsageException(e.Message);
        }
        int code = ImportWorker.RunWorker(args[0], args[1], options);
        if (code != 0) error.WriteLine($"'{Path.GetFileName(args[0])}' could not be imported.");
        return code == 0 ? Program.ExitOk : Program.ExitInputErrors;
    }
}
