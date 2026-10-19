using Candela.Electrical;
using Candela.Gdtf;

namespace Candela.App.Tests;

/// <summary>
/// The generic cabling devices (splitters, nodes, dimmer) in the shared test libraries: their GDTF files are written
/// once per test run into a folder that outlives every test, so an engine's own registration (into its private, deleted
/// cache folder) never leaves the shared library pointing at deleted files.
/// </summary>
internal static class GenericDeviceFiles
{
    private static readonly Lazy<string> s_folder = new(() =>
    {
        string folder = Path.Combine(Path.GetTempPath(), "candela-app-tests-generic-devices");
        Directory.CreateDirectory(folder);
        foreach (var device in GenericDevices.All)
        {
            string path = Path.Combine(folder, device.FileName);
            var bytes = GenericDevices.GdtfBytes(device);
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                continue;
            string temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }

        return folder;
    });

    /// <summary>Adds the generic devices to <paramref name="library"/> and returns it.</summary>
    public static FixtureLibrary AddTo(FixtureLibrary library)
    {
        library.AddFiles(GenericDevices.All.Select(d => Path.Combine(s_folder.Value, d.FileName)));
        return library;
    }
}
