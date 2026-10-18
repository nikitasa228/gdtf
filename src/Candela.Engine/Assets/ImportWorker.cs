using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Candela.Assets;

namespace Candela.Engine;

/// <summary>Outcome of one model import in the isolated worker process.</summary>
public enum ImportWorkerStatus
{
    /// <summary>The worker imported the model and wrote its package.</summary>
    Succeeded,

    /// <summary>The worker ran and reported that the file cannot be imported (unsupported, damaged…); see the message.</summary>
    Rejected,

    /// <summary>The worker ended abnormally (a crash inside the native importer, killed, unreadable output).</summary>
    Crashed,

    /// <summary>The worker took longer than the timeout and was stopped.</summary>
    TimedOut,

    /// <summary>No worker program was found (or it could not be started); the caller may import in-process.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="ImportWorker.Run"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Model">The imported model (texture keys relative to the model, no prefix) when <see cref="Status"/> is Succeeded.</param>
/// <param name="Message">User-facing explanation for every other status.</param>
public sealed record ImportWorkerResult(ImportWorkerStatus Status, ImportedModel? Model, string Message);

/// <summary>
/// Imports 3D models that go through the native Assimp library (OBJ, FBX, COLLADA, 3DS) in a separate process —
/// <c>candela import-worker &lt;input&gt; &lt;output-dir&gt;</c> — so that a crafted or damaged file that crashes the native
/// code ends the worker instead of the application. The worker writes a <see cref="ModelPackage"/>; a crash, a hang
/// (stopped after the timeout) or an unreadable package becomes a <see cref="ImportWorkerResult"/> the engine reports as
/// "the file could not be imported safely".
/// </summary>
/// <remarks>
/// The worker program is searched for in this order: <see cref="StageEngineOptions.ImportWorkerPath"/>, the
/// <c>CANDELA_IMPORT_WORKER</c> environment variable, then <c>candela</c> / <c>candela.exe</c> / <c>candela.dll</c>
/// next to the running program. A <c>.dll</c> is started through the <c>dotnet</c> host that runs this process.
/// </remarks>
public static class ImportWorker
{
    /// <summary>Command name of the hidden CLI mode.</summary>
    public const string CommandName = "import-worker";

    /// <summary>Name of the package the worker writes into its output folder.</summary>
    public const string PackageFileName = "model" + ModelPackage.Extension;

    /// <summary>Name of the file holding the user-facing reason when the worker rejects a file.</summary>
    public const string ErrorFileName = "error.txt";

    /// <summary>Exit code of a worker that rejected the file (and wrote <see cref="ErrorFileName"/>).</summary>
    public const int ExitRejected = 4;

    /// <summary>Environment variable that names the worker program.</summary>
    public const string EnvironmentVariable = "CANDELA_IMPORT_WORKER";

    /// <summary>How long the worker's output is read after it ended (child processes may hold the pipes open).</summary>
    public static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True for formats decoded by the native Assimp library (OBJ, FBX, COLLADA, 3DS), which the engine imports in the
    /// worker; glTF/GLB (SharpGLTF), STL and PLY (Candela's own readers) are managed code and stay in-process.
    /// </summary>
    public static bool UsesNativeImporter(string fileName) =>
        AssetFormats.GetModelFormat(fileName) is ModelFormat.Obj or ModelFormat.Fbx or ModelFormat.Collada or ModelFormat.ThreeDs;

    /// <summary>
    /// The worker command line for <paramref name="input"/> → <paramref name="outputDirectory"/> with the persistent
    /// import options (up axis, unit scale, unit guessing, smoothing angle, part limit).
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string input, string outputDirectory, ImportOptions? options)
    {
        var o = options ?? ImportOptions.Default;
        var args = new List<string> { CommandName, input, outputDirectory, "--up-axis", o.UpAxis.ToString().ToLowerInvariant() };
        if (o.UnitScale is { } scale) args.AddRange(new[] { "--unit-scale", scale.ToString("R", CultureInfo.InvariantCulture) });
        if (!o.GuessUnits) args.Add("--no-guess-units");
        args.AddRange(new[] { "--smoothing", o.SmoothingAngleDegrees.ToString("R", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "--max-parts", o.MaxParts.ToString(CultureInfo.InvariantCulture) });
        return args;
    }

    /// <summary>
    /// Parses the options after <c>import-worker &lt;input&gt; &lt;output-dir&gt;</c> (see <see cref="BuildArguments"/>).
    /// Throws <see cref="FormatException"/> for an unknown option or an invalid value.
    /// </summary>
    public static ImportOptions ParseOptions(IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = ImportOptions.Default;
        for (int i = 0; i < options.Count; i++)
        {
            string name = options[i];
            string Value() => i + 1 < options.Count ? options[++i] : throw new FormatException($"Option {name} needs a value.");
            switch (name)
            {
                case "--up-axis":
                    o = o with
                    {
                        UpAxis = Enum.TryParse<SourceUpAxis>(Value(), ignoreCase: true, out var axis) ? axis : throw new FormatException("--up-axis must be auto, y or z."),
                    };
                    break;
                case "--unit-scale":
                    o = o with { UnitScale = PositiveFloat(name, Value()) };
                    break;
                case "--no-guess-units":
                    o = o with { GuessUnits = false };
                    break;
                case "--smoothing":
                    o = o with { SmoothingAngleDegrees = Math.Clamp(PositiveFloat(name, Value()), 0f, 180f) };
                    break;
                case "--max-parts":
                    o = o with
                    {
                        MaxParts = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
                            ? n
                            : throw new FormatException("--max-parts must be a positive integer."),
                    };
                    break;
                default:
                    throw new FormatException($"Unknown option '{name}'.");
            }
        }
        return o;
    }

    /// <summary>
    /// The worker side (called by <c>candela import-worker</c>): imports <paramref name="input"/> with
    /// <see cref="ModelImporter"/> (texture keys left relative) and writes <see cref="PackageFileName"/> into
    /// <paramref name="outputDirectory"/>. Returns 0 on success, <see cref="ExitRejected"/> after writing the reason to
    /// <see cref="ErrorFileName"/> when the file cannot be imported. A native crash ends the process instead, which is
    /// the point of running it separately.
    /// </summary>
    public static int RunWorker(string input, string outputDirectory, ImportOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var model = ModelImporter.Import(input, options with { TextureKeyPrefix = "" });
            ModelPackage.WriteFile(model, Path.Combine(outputDirectory, PackageFileName));
            return 0;
        }
        catch (Exception e) when (e is AssetImportException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            File.WriteAllText(Path.Combine(outputDirectory, ErrorFileName), e.Message);
            return ExitRejected;
        }
    }

    /// <summary>
    /// Finds the worker program. A <paramref name="configuredPath"/> is used alone (null when that file does not exist, so
    /// a configured but missing worker falls back to in-process imports); otherwise <see cref="EnvironmentVariable"/>, then
    /// the <c>candela</c> program next to the running one. Null when none exists.
    /// </summary>
    public static string? FindWorker(string? configuredPath = null)
    {
        if (configuredPath is not null) return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment && File.Exists(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);
        string dir = AppContext.BaseDirectory;
        string[] names = OperatingSystem.IsWindows() ? new[] { "candela.exe", "candela.dll" } : new[] { "candela", "candela.dll" };
        foreach (var name in names)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Runs the worker on <paramref name="input"/> and reads its package. Never throws for a failing import: every
    /// outcome is a <see cref="ImportWorkerResult"/>. The temporary output folder is removed afterwards.
    /// </summary>
    public static ImportWorkerResult Run(string workerPath, string input, ImportOptions? options, TimeSpan timeout, string tempRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(workerPath);
        ArgumentException.ThrowIfNullOrEmpty(input);
        string output = Path.Combine(tempRoot, "worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(output);
            var start = CreateStartInfo(workerPath, BuildArguments(Path.GetFullPath(input), output, options));
            using var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) return new ImportWorkerResult(ImportWorkerStatus.Unavailable, null, "The import worker could not be started.");
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return new ImportWorkerResult(ImportWorkerStatus.Unavailable, null, $"The import worker could not be started: {e.Message}");
            }
            // Drain both pipes so a chatty native library cannot block the worker on a full pipe buffer.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ClampTimeout(timeout)))
            {
                TryKill(process);
                return new ImportWorkerResult(ImportWorkerStatus.TimedOut, null,
                    $"importing took longer than {timeout.TotalSeconds:0} s and was stopped");
            }
            // The pipes reach their end only when every process holding them has ended: a helper the importer left
            // behind (or a crash reporter) can keep them open long after the worker died, so its last words are
            // waited for only briefly — the timeout bounds the whole import.
            string diagnostics = Diagnostics(stdout, stderr);
            string package = Path.Combine(output, PackageFileName);
            string error = Path.Combine(output, ErrorFileName);
            if (process.ExitCode == ExitRejected && File.Exists(error))
                return new ImportWorkerResult(ImportWorkerStatus.Rejected, null, SafeRead(error));
            if (process.ExitCode != 0 || !File.Exists(package))
            {
                string why = process.ExitCode == 0 ? "it produced no result" : $"it ended with code {process.ExitCode}";
                return new ImportWorkerResult(ImportWorkerStatus.Crashed, null,
                    diagnostics.Length > 0 ? $"the importer failed ({why}): {diagnostics}" : $"the importer failed ({why})");
            }
            try
            {
                return new ImportWorkerResult(ImportWorkerStatus.Succeeded, ModelPackage.ReadFile(package), "");
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new ImportWorkerResult(ImportWorkerStatus.Crashed, null, $"the importer's result is unreadable: {e.Message}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ImportWorkerResult(ImportWorkerStatus.Unavailable, null, $"the import worker could not be used: {e.Message}");
        }
        finally
        {
            TryDeleteDirectory(output);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string workerPath, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = DotnetHost();
            start.ArgumentList.Add(workerPath);
        }
        else
        {
            start.FileName = workerPath;
        }
        foreach (var a in arguments) start.ArgumentList.Add(a);
        return start;
    }

    /// <summary>The <c>dotnet</c> host that runs this process (for starting a framework-dependent <c>candela.dll</c>).</summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        if (Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return self;
        // <root>/shared/Microsoft.NETCore.App/<version>/ → <root>/dotnet
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var root = new DirectoryInfo(runtimeDir).Parent?.Parent?.Parent;
        if (root is not null)
        {
            string candidate = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    private static int ClampTimeout(TimeSpan timeout)
    {
        double ms = timeout.TotalMilliseconds;
        return ms <= 0 || double.IsNaN(ms) ? 1 : ms >= int.MaxValue ? int.MaxValue : (int)ms;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process ended on its own between the timeout and the kill.
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return text.Length > 2000 ? text[..2000] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "the file cannot be imported";
        }
    }

    /// <summary>The end of the worker's error output (else its standard output), or "" when it cannot be read in time.</summary>
    private static string Diagnostics(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            if (!Task.WhenAll(stdout, stderr).Wait(OutputDrainTimeout)) return "";
        }
        catch (AggregateException)
        {
            return "";
        }
        return Tail(stderr.Result) is { Length: > 0 } err ? err : Tail(stdout.Result);
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        if (text.Length <= 400) return text;
        int cut = text.LastIndexOf('\n', text.Length - 1, 400);
        return text[(cut >= 0 ? cut + 1 : text.Length - 400)..];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary folder that cannot be removed (virus scanner holding a file) is left for the OS to clean.
        }
    }

    private static float PositiveFloat(string name, string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) && f > 0f
            ? f
            : throw new FormatException($"{name} must be a positive number.");
}
