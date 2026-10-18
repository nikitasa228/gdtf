using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Rendering;

namespace Candela.App.Shell;

/// <summary>The transform tool of the viewports.</summary>
internal enum EditorTool
{
    /// <summary>Click / box selection only.</summary>
    Select,

    /// <summary>Translate gizmo on three axes.</summary>
    Move,

    /// <summary>Rotate gizmo on three axes.</summary>
    Rotate,
}

/// <summary>Editing state shared by all viewports, menus and shortcuts.</summary>
internal sealed class ToolState
{
    /// <summary>Default rotation snapping step, degrees.</summary>
    public const float DefaultAngleStep = 15f;

    /// <summary>Active tool (W/E/Q).</summary>
    public EditorTool Tool { get; set; } = EditorTool.Move;

    /// <summary>Gizmo axes follow the object (true) or the world (false).</summary>
    public bool LocalSpace { get; set; }

    /// <summary>Snapping on (Ctrl while dragging inverts it).</summary>
    public bool Snap { get; set; }

    /// <summary>Rotation snapping step, degrees.</summary>
    public float AngleStep { get; set; } = DefaultAngleStep;

    /// <summary>Real-time quality settings shared by every viewport (the Preview panel).</summary>
    public PreviewSettings Preview { get; } = new();

    /// <summary>Final-render settings (the Render panel).</summary>
    public FinalRenderSettings FinalRender { get; } = new();
}

/// <summary>Services the workspace and its panels share.</summary>
internal sealed record WorkspaceContext(
    AppLog Log,
    SettingsStore Settings,
    EditorSession Session,
    GpuDevice Gpu,
    ImGuiHost ImGui,
    FontSet Fonts,
    MainWindow Window,
    EditorApplication Application,
    FrameTimer Timer)
{
    /// <summary>Tool and quality state.</summary>
    public ToolState Tools { get; } = new();
}
