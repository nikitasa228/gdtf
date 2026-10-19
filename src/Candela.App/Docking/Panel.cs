using System.Numerics;
using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Docking;

/// <summary>
/// A dockable panel: a Dear ImGui window that can be docked as a tab into any other panel group, split into zones or
/// torn off into its own OS window. The window name is <c>"&lt;icon&gt;  &lt;title&gt;###&lt;id&gt;"</c>: the stable id after
/// <c>###</c> keeps the layout valid when the language (title) changes.
/// </summary>
internal abstract class Panel
{
    private string _windowName = "";
    private string _cachedTitle = "";
    private string _menuLabel = "";

    /// <summary>Creates a panel with a stable <paramref name="id"/> and an icon from <see cref="Icons"/>.</summary>
    protected Panel(string id, string icon)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
        Icon = icon;
    }

    /// <summary>Stable id (layout files, Window menu, scripts).</summary>
    public string Id { get; }

    /// <summary>Icon glyph shown in tabs and in the icon column.</summary>
    public string Icon { get; }

    /// <summary>Localised title.</summary>
    public abstract string Title { get; }

    /// <summary>Whether the panel is shown (closing the tab sets it to false; the Window menu reopens it).</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>Whether the user may close the panel (the last viewport stays open).</summary>
    public virtual bool CanClose => true;

    /// <summary>Content drawn edge to edge (viewports) instead of with the window padding.</summary>
    public virtual bool EdgeToEdge => false;

    /// <summary>Extra window flags (e.g. no scrollbar for viewports).</summary>
    public virtual ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.None;

    /// <summary>Size of the window when it first appears floating, in pixels at 100 %.</summary>
    public virtual Vector2 DefaultSize => new(420, 340);

    /// <summary>The ImGui window name (icon + title + stable id), rebuilt when the title changes.</summary>
    public string WindowName
    {
        get
        {
            string title = Title;
            if (!ReferenceEquals(title, _cachedTitle) && title != _cachedTitle || _windowName.Length == 0)
            {
                _cachedTitle = title;
                _windowName = $"{Icon}  {title}###{Id}";
                _menuLabel = $"{Icon}  {title}";
            }

            return _windowName;
        }
    }

    /// <summary>The Window-menu label (icon + title), rebuilt with the title.</summary>
    public string MenuLabel
    {
        get
        {
            _ = WindowName;
            return _menuLabel;
        }
    }

    /// <summary>The id part used by ImGui for docking (<c>###Id</c>).</summary>
    public string DockName => "###" + Id;

    /// <summary>The ImGui frame in which the panel's content was last on screen (its tab active), or −1.</summary>
    public int LastShownFrame { get; set; } = -1;

    /// <summary>The dock node the panel's window was last drawn in (0: floating or never drawn).</summary>
    public uint DockNodeId { get; set; }

    /// <summary>True when the panel's content was on screen in the previous or the current frame.</summary>
    public bool IsShown => IsOpen && LastShownFrame >= ImGui.GetFrameCount() - 1;

    /// <summary>Exception that stopped the panel, shown instead of the content until the user retries.</summary>
    public Exception? Fault { get; set; }

    /// <summary>Draws the panel's content inside its window (and child region).</summary>
    public abstract void Draw();

    /// <summary>Called when the panel was closed by the user (release GPU resources etc.).</summary>
    public virtual void OnClosed()
    {
    }
}
