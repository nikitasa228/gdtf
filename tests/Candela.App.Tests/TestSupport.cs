using System.Numerics;
using Candela.App.Diagnostics;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>A unique temporary folder, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "candela-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string relative, string text = "")
    {
        string full = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>Helpers shared by the tests.</summary>
internal static class TestLog
{
    /// <summary>A log that keeps everything in memory and prints nothing.</summary>
    public static AppLog Create() => new() { ConsoleLevel = LogLevel.Error + 1 }; // above every level: silent

    /// <summary>The messages of a log.</summary>
    public static List<LogEntry> Entries(AppLog log)
    {
        var list = new List<LogEntry>();
        log.CopyTo(list);
        return list;
    }
}

/// <summary>Tests that use the process-wide Dear ImGui context or global UI state run one after another.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiStateCollection
{
    public const string Name = "UI state";
}

/// <summary>
/// A Dear ImGui context without a window or GPU: frames are built (layout, docking, input) but never rendered. The
/// context claims texture support so the font atlas needs no renderer.
/// </summary>
internal sealed unsafe class HeadlessImGui : IDisposable
{
    private readonly ImGuiContextPtr _context;

    public HeadlessImGui(Vector2 displaySize)
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        io.IniFilename = null;
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        io.DisplaySize = displaySize;
        io.DeltaTime = 1f / 60f;
        io.Fonts.AddFontDefault();
    }

    public ImGuiContextPtr Context => _context;

    /// <summary>Runs one frame: <paramref name="draw"/> between NewFrame and Render.</summary>
    public void Frame(Action draw)
    {
        ImGui.SetCurrentContext(_context);
        ImGui.GetIO().DeltaTime = 1f / 60f;
        ImGui.NewFrame();
        draw();
        ImGui.Render();
    }

    public void Dispose()
    {
        ImGui.SetCurrentContext(_context);
        ImGui.DestroyContext(_context);
    }
}
