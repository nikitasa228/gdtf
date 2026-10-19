using Candela.Core.Diagnostics;
using Candela.Electrical;

namespace Candela.Engine;

/// <summary>
/// Makes the generic cabling devices of <see cref="GenericDevices"/> (DMX splitters, Art-Net / sACN nodes, the dimmer
/// pack) part of an engine's fixture library: their GDTF files are written once into
/// <c>{cache}/generic-devices/</c> (byte-identical on every run, so thumbnails and parsed types stay cached) and added
/// to <see cref="StageEngine.Library"/>, where the DMX engine, the renderer, the electrical solver and project saving
/// (which copies every GDTF a scene uses) find them like any other fixture type. Call it once after creating the
/// engine, before a scene that uses them is opened; files the library already has (a project's copies) are left alone.
/// </summary>
public static class GenericDeviceLibrary
{
    /// <summary>Folder under the engine's cache directory that holds the generated files.</summary>
    public const string FolderName = "generic-devices";

    /// <summary>Code of the issue reported when the files cannot be written.</summary>
    public const string IssueCode = "engine.generic-devices";

    /// <summary>
    /// Writes the generic devices' GDTF files (when missing or different) and adds those the library does not know yet.
    /// Returns the number added; a folder that cannot be written is reported as an issue (<c>engine.generic-devices</c>)
    /// and the library stays as it was.
    /// </summary>
    public static int Register(StageEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var paths = new List<string>(GenericDevices.All.Count);
        string folder = Path.Combine(engine.CacheDirectory, FolderName);
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var device in GenericDevices.All)
            {
                if (engine.Library.FindByFileName(device.FileName) is not null) continue;
                string path = Path.Combine(folder, device.FileName);
                var bytes = GenericDevices.GdtfBytes(device);
                if (!SameContent(path, bytes))
                {
                    // Written aside and renamed, so a concurrent reader (a second editor) never sees half a file.
                    string temp = path + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, path, overwrite: true);
                }
                paths.Add(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            engine.Issues.Report(new Issue(IssueSeverity.Warning, IssueCategory.Program, IssueCode,
                $"The generic cabling devices (splitters, nodes, dimmer) could not be written to '{folder}': {e.Message}"));
            return 0;
        }

        return paths.Count == 0 ? 0 : engine.Library.AddFiles(paths);
    }

    private static bool SameContent(string path, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != bytes.Length) return false;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
