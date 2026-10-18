using System.Numerics;
using System.Text;
using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Docking;

/// <summary>Built-in layouts the dock builder can create.</summary>
internal enum LayoutPreset
{
    /// <summary>Library left, viewport centre, properties right, issues/console bottom.</summary>
    Default,

    /// <summary>Like <see cref="Default"/> with four viewports (top, front, right, perspective) in the centre.</summary>
    FourViewports,
}

/// <summary>Which panels go into which zone of a preset layout (panel dock names, <c>###Id</c>).</summary>
internal sealed class LayoutPlan
{
    /// <summary>Panels in the left column.</summary>
    public List<string> Left { get; } = new();

    /// <summary>Panels in the right column (properties first).</summary>
    public List<string> Right { get; } = new();

    /// <summary>Panels in the bottom strip.</summary>
    public List<string> Bottom { get; } = new();

    /// <summary>Viewports in the centre (one fills it; four make a 2 × 2 grid).</summary>
    public List<string> Center { get; } = new();
}

/// <summary>
/// The docking workspace over the main window: the dock space (every panel is a dockable window with ImGui's drop-zone
/// indicators; torn-off panels become OS windows), the preset layouts built with the dock builder, and the per-group
/// tab style (requirement): a small corner button on each panel group switches between ImGui's tab bar on top
/// (icon + name per tab) and a vertical icon column on the left (the group's tab bar is hidden with the node flag
/// <c>NoTabBar</c>, which ImGui also persists, and the column selects which panel the group shows).
/// </summary>
internal sealed unsafe class DockWorkspace
{
    /// <summary>Id of the main dock space node (any constant; stable across sessions so layouts stay valid).</summary>
    public const uint DockSpaceId = 0x43414E44; // "CAND"

    /// <summary>Width of the icon column at 100 % (pixels).</summary>
    public const float IconColumnWidth = 38f;

    // imgui_internal.h ImGuiDockNodeFlagsPrivate_ (not in the public enum of the bindings).
    private const ImGuiDockNodeFlags DockSpaceFlag = (ImGuiDockNodeFlags)(1 << 10);
    private const ImGuiDockNodeFlags NoTabBarFlag = (ImGuiDockNodeFlags)(1 << 12);

    private readonly TabStyleMap _styles = new();
    private readonly Dictionary<uint, Panel> _panelsByWindowId = new();
    private readonly Dictionary<Panel, uint> _windowIds = new();
    private readonly List<Panel> _columnPanels = new();
    private LayoutPreset? _pendingPreset;
    private LayoutPlan? _pendingPlan;
    private uint _menuNode;
    private Panel? _menuPanel;
    private bool _openMenu;

    /// <summary>Tab styles per dock node.</summary>
    public TabStyleMap TabStyles => _styles;

    /// <summary>Raised when the user changed the tab style of a group (the workspace layout is then saved on exit).</summary>
    public event Action? LayoutChanged;

    /// <summary>Asks for a preset layout to be built at the next <see cref="DrawDockSpace"/>.</summary>
    public void RequestPreset(LayoutPreset preset, LayoutPlan plan)
    {
        _pendingPreset = preset;
        _pendingPlan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>True while a preset is waiting to be built.</summary>
    public bool HasPendingPreset => _pendingPreset is not null;

    /// <summary>Replaces the tab styles (a layout was loaded).</summary>
    public void ReplaceTabStyles(TabStyleMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _styles.Clear();
        foreach (var id in map.NodeIds)
        {
            var state = map.Get(id);
            _styles.SetStyle(id, state.Style);
            _styles.SetSelected(id, state.SelectedPanel);
        }
    }

    /// <summary>
    /// Applies the stored tab styles to the dock nodes (call right after ImGui's NewFrame, before the dock space and the
    /// panels): icon-column nodes get the hidden tab bar and show their selected panel first (ImGui shows the first
    /// window of a node without tab bar); top-tab nodes get their tab bar back.
    /// </summary>
    public void ApplyTabStyles()
    {
        foreach (var id in _styles.NodeIds)
        {
            var node = ImGuiP.DockBuilderGetNode(id);
            if (node.IsNull || node.Windows.Size == 0)
                continue;
            var state = _styles.Get(id);
            bool hasFlag = (node.LocalFlags & NoTabBarFlag) != 0;
            if (state.Style == TabStyle.LeftIcons)
            {
                if (!hasFlag)
                    SetNoTabBar(node, true);
                MoveSelectedFirst(node, state.SelectedPanel);
            }
            else if (hasFlag)
            {
                SetNoTabBar(node, false);
                if (state.SelectedPanel is { } selected && FindWindow(node, selected) is { } w)
                    node.SelectedTabId = w.TabId;
            }
        }
    }

    /// <summary>Builds a pending preset layout and submits the dock space over the main viewport's work area.</summary>
    public void DrawDockSpace()
    {
        var viewport = ImGui.GetMainViewport();
        if (_pendingPreset is { } preset && _pendingPlan is { } plan)
        {
            _pendingPreset = null;
            _pendingPlan = null;
            BuildPreset(preset, plan, viewport.WorkSize);
        }

        ImGui.DockSpaceOverViewport(DockSpaceId, viewport, ImGuiDockNodeFlags.None);
    }

    /// <summary>
    /// Draws one panel: its window, the group chrome for its dock node (corner tab-style button, or the icon column)
    /// and the content child. <paramref name="drawContent"/> runs inside the content region and is isolated by the
    /// caller against exceptions.
    /// </summary>
    public void DrawPanel(Panel panel, IReadOnlyList<Panel> allPanels, float scale, Action<Panel> drawContent)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ImGui.SetNextWindowSize(panel.DefaultSize * scale, ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = true;
        var flags = panel.WindowFlags | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        bool visible = panel.CanClose ? ImGui.Begin(panel.WindowName, &open, flags) : ImGui.Begin(panel.WindowName, flags);
        ImGui.PopStyleVar();
        var window = ImGuiP.GetCurrentWindow();
        if (!window.IsNull)
            Register(panel, window.ID);

        if (visible)
        {
            var node = ImGuiP.GetWindowDockNode();
            var style = TabStyle.TopTabs;
            if (!node.IsNull && window.DockIsActive)
            {
                style = AdoptStyle(node);
                if (style == TabStyle.TopTabs)
                    DrawCornerButton(node, panel);
                else
                    DrawIconColumn(node, panel, allPanels, scale);
            }

            DrawContentChild(panel, style == TabStyle.LeftIcons, drawContent);
        }

        ImGui.End();
        if (!open)
        {
            panel.IsOpen = false;
            panel.OnClosed();
        }
    }

    /// <summary>Draws the tab-style popup when a corner button or icon-column button asked for it (top level, after the panels).</summary>
    public void DrawTabStyleMenu(IReadOnlyList<Panel> panels, Action<Panel> focusPanel, Action<Panel> undockPanel)
    {
        const string popupId = "##tab-style-menu";
        if (_openMenu)
        {
            _openMenu = false;
            ImGui.OpenPopup(popupId);
        }

        if (!ImGui.BeginPopup(popupId))
            return;
        var node = _menuNode != 0 ? ImGuiP.DockBuilderGetNode(_menuNode) : default;
        if (node.IsNull)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextDisabled(Loc.Get(Str.TabStyle));
        var style = _styles.StyleOf(_menuNode);
        if (ImGui.RadioButton($"{Icons.PanelTop}  {Loc.Get(Str.TabStyleTop)}", style == TabStyle.TopTabs))
            SetStyle(node, TabStyle.TopTabs);
        if (ImGui.RadioButton($"{Icons.PanelLeft}  {Loc.Get(Str.TabStyleLeft)}", style == TabStyle.LeftIcons))
            SetStyle(node, TabStyle.LeftIcons);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextUnformatted(Loc.Get(Str.TabStyleHint));
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.TextDisabled(Loc.Get(Str.DockPanelsInGroup));
        CollectNodePanels(node, panels, _columnPanels);
        var visibleWindow = node.VisibleWindow;
        foreach (var p in _columnPanels)
        {
            bool isVisible = !visibleWindow.IsNull && _windowIds.TryGetValue(p, out uint wid) && wid == visibleWindow.ID;
            if (ImGui.MenuItem($"{p.Icon}  {p.Title}", "", isVisible))
            {
                SelectInNode(node, p);
                focusPanel(p);
            }
        }

        if (_menuPanel is { } current)
        {
            ImGui.Separator();
            if (ImGui.MenuItem($"{Icons.ExternalLink}  {Loc.Get(Str.DockUndockPanel)}"))
                undockPanel(current);
            if (current.CanClose && ImGui.MenuItem($"{Icons.X}  {Loc.Get(Str.DockClosePanel)}"))
            {
                current.IsOpen = false;
                current.OnClosed();
            }
        }

        ImGui.EndPopup();
    }

    /// <summary>Forgets tab styles of nodes that no longer exist (before saving a layout).</summary>
    public void PruneTabStyles() => _styles.Prune(id => !ImGuiP.DockBuilderGetNode(id).IsNull);

    /// <summary>The ImGui window id of a panel (stable: the hash of <c>###Id</c>).</summary>
    public static uint WindowIdOf(Panel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var bytes = Encoding.UTF8.GetBytes(panel.DockName + "\0");
        fixed (byte* p = bytes)
            return ImGuiP.ImHashStr(p);
    }

    /// <summary>Undocks a panel's window (it becomes a floating window, or its own OS window with multi-viewport).</summary>
    public static void Undock(Panel panel)
    {
        var window = ImGuiP.FindWindowByID(WindowIdOf(panel));
        if (window.IsNull || window.DockNode.IsNull)
            return;
        ImGuiP.DockContextQueueUndockWindow(ImGui.GetCurrentContext(), window);
    }

    /// <summary>Puts the tab-style map in a layout file and restores it.</summary>
    internal string SerializeTabStyles()
    {
        PruneTabStyles();
        return _styles.Serialize();
    }

    private void Register(Panel panel, uint windowId)
    {
        if (_windowIds.TryGetValue(panel, out uint existing) && existing == windowId)
            return;
        _windowIds[panel] = windowId;
        _panelsByWindowId[windowId] = panel;
    }

    /// <summary>Keeps the map in sync with node flags ImGui changed by itself (a split transfers the hidden tab bar).</summary>
    private TabStyle AdoptStyle(ImGuiDockNodePtr node)
    {
        bool hasFlag = (node.LocalFlags & NoTabBarFlag) != 0;
        var mapped = _styles.StyleOf(node.ID);
        if (hasFlag && mapped != TabStyle.LeftIcons)
        {
            _styles.SetStyle(node.ID, TabStyle.LeftIcons);
            return TabStyle.LeftIcons;
        }

        if (!hasFlag && mapped == TabStyle.LeftIcons)
            return TabStyle.LeftIcons; // applied at the start of the next frame
        return mapped;
    }

    private void SetStyle(ImGuiDockNodePtr node, TabStyle style)
    {
        uint id = node.ID;
        if (style == TabStyle.LeftIcons && !node.VisibleWindow.IsNull && _panelsByWindowId.TryGetValue(node.VisibleWindow.ID, out var visible))
            _styles.SetSelected(id, visible.Id);
        _styles.SetStyle(id, style);
        LayoutChanged?.Invoke();
    }

    private void SelectInNode(ImGuiDockNodePtr node, Panel panel)
    {
        _styles.SetSelected(node.ID, panel.Id);
        if (_styles.StyleOf(node.ID) == TabStyle.TopTabs && FindWindow(node, panel.Id) is { } w)
            node.SelectedTabId = w.TabId;
        LayoutChanged?.Invoke();
    }

    private void DrawCornerButton(ImGuiDockNodePtr node, Panel panel)
    {
        if (!ImGuiP.DockNodeBeginAmendTabBar(node))
            return;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        if (ImGui.TabItemButton(Icons.EllipsisVertical + "##tab-style", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
            RequestMenu(node, panel);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get(Str.TabStyle));
        ImGuiP.DockNodeEndAmendTabBar();
    }

    private void DrawIconColumn(ImGuiDockNodePtr node, Panel current, IReadOnlyList<Panel> allPanels, float scale)
    {
        float width = MathF.Round(IconColumnWidth * scale);
        float height = ImGui.GetContentRegionAvail().Y;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.Rgb(0x18191C)));
        drawList.AddLine(origin + new Vector2(width - 0.5f, 0), origin + new Vector2(width - 0.5f, height), Theme.U32(Theme.Rgb(0x2C2E33)));

        CollectNodePanels(node, allPanels, _columnPanels);
        float cell = width;
        var visibleId = node.VisibleWindow.IsNull ? 0u : node.VisibleWindow.ID;
        for (int i = 0; i < _columnPanels.Count; i++)
        {
            var p = _columnPanels[i];
            var pos = origin + new Vector2(0, i * cell);
            ImGui.SetCursorScreenPos(pos);
            ImGui.PushID(p.Id);
            ImGui.InvisibleButton("##icon", new Vector2(cell, cell));
            bool hovered = ImGui.IsItemHovered();
            bool selected = _windowIds.TryGetValue(p, out uint wid) ? wid == visibleId : ReferenceEquals(p, current);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                SelectInNode(node, p);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, cell * 0.5f))
            {
                var w = ImGuiP.FindWindowByID(WindowIdOf(p));
                if (!w.IsNull)
                {
                    ImGuiP.DockContextQueueUndockWindow(ImGui.GetCurrentContext(), w);
                    ImGuiP.StartMouseMovingWindow(w);
                }
            }

            if (hovered)
                ImGui.SetTooltip(p.Title);
            if (selected || hovered)
                drawList.AddRectFilled(pos, pos + new Vector2(cell, cell), Theme.U32(selected ? Theme.Rgb(0x2A2C31) : Theme.Rgb(0x24262A)));
            if (selected)
                drawList.AddRectFilled(pos, pos + new Vector2(MathF.Max(2f, 2f * scale), cell), Theme.U32(Theme.Accent));
            var textSize = ImGui.CalcTextSize(p.Icon);
            drawList.AddText(pos + (new Vector2(cell, cell) - textSize) * 0.5f, Theme.U32(selected ? Theme.Text : Theme.TextMuted), p.Icon);
            ImGui.PopID();
        }

        // Corner setting at the bottom of the column.
        var buttonPos = origin + new Vector2(0, MathF.Max(height - cell, _columnPanels.Count * cell));
        ImGui.SetCursorScreenPos(buttonPos);
        if (ImGui.InvisibleButton("##tab-style-column", new Vector2(cell, cell)))
            RequestMenu(node, current);
        bool menuHovered = ImGui.IsItemHovered();
        if (menuHovered)
            ImGui.SetTooltip(Loc.Get(Str.TabStyle));
        var glyph = Icons.EllipsisVertical;
        var glyphSize = ImGui.CalcTextSize(glyph);
        drawList.AddText(buttonPos + (new Vector2(cell, cell) - glyphSize) * 0.5f, Theme.U32(menuHovered ? Theme.Text : Theme.TextMuted), glyph);

        ImGui.SetCursorScreenPos(origin + new Vector2(width, 0));
    }

    private static void DrawContentChild(Panel panel, bool besideColumn, Action<Panel> drawContent)
    {
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 1 || avail.Y < 1)
            return;
        var childFlags = panel.EdgeToEdge ? ImGuiChildFlags.None : ImGuiChildFlags.AlwaysUseWindowPadding;
        var windowFlags = panel.EdgeToEdge ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None;
        if (!panel.EdgeToEdge)
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, ImGui.GetStyle().WindowPadding);
        bool open = ImGui.BeginChild("##content", avail, childFlags, windowFlags);
        if (!panel.EdgeToEdge)
            ImGui.PopStyleVar();
        if (open)
            drawContent(panel);
        ImGui.EndChild();
        _ = besideColumn;
    }

    private void RequestMenu(ImGuiDockNodePtr node, Panel panel)
    {
        _menuNode = node.ID;
        _menuPanel = panel;
        _openMenu = true;
    }

    private void CollectNodePanels(ImGuiDockNodePtr node, IReadOnlyList<Panel> allPanels, List<Panel> result)
    {
        result.Clear();
        var windows = node.Windows;
        // Registry order (not ImGui's window order, which changes as the selected panel is moved first).
        foreach (var p in allPanels)
        {
            if (!p.IsOpen || !_windowIds.TryGetValue(p, out uint wid))
                continue;
            for (int i = 0; i < windows.Size; i++)
            {
                if (!windows.Data[i].IsNull && windows.Data[i].ID == wid)
                {
                    result.Add(p);
                    break;
                }
            }
        }
    }

    private ImGuiWindowPtr? FindWindow(ImGuiDockNodePtr node, string panelId)
    {
        var windows = node.Windows;
        for (int i = 0; i < windows.Size; i++)
        {
            var w = windows.Data[i];
            if (!w.IsNull && _panelsByWindowId.TryGetValue(w.ID, out var p) && p.Id == panelId)
                return w;
        }

        return null;
    }

    private void MoveSelectedFirst(ImGuiDockNodePtr node, string? panelId)
    {
        if (panelId is null)
            return;
        var windows = node.Windows;
        for (int i = 1; i < windows.Size; i++)
        {
            var w = windows.Data[i];
            if (!w.IsNull && _panelsByWindowId.TryGetValue(w.ID, out var p) && p.Id == panelId)
            {
                (windows.Data[0], windows.Data[i]) = (windows.Data[i], windows.Data[0]);
                return;
            }
        }
    }

    private static void SetNoTabBar(ImGuiDockNodePtr node, bool on)
    {
        if (on)
            node.LocalFlags |= NoTabBarFlag;
        else
            node.LocalFlags &= ~NoTabBarFlag;
        node.MergedFlags = node.SharedFlags | node.LocalFlags | node.LocalFlagsInWindows;
    }

    private void BuildPreset(LayoutPreset preset, LayoutPlan plan, Vector2 size)
    {
        _styles.Clear();
        ImGuiP.DockBuilderRemoveNode(DockSpaceId);
        ImGuiP.DockBuilderAddNode(DockSpaceId, DockSpaceFlag);
        ImGuiP.DockBuilderSetNodeSize(DockSpaceId, Vector2.Max(size, new Vector2(320, 240)));
        uint left, rest, right, center, bottom, main;
        ImGuiP.DockBuilderSplitNode(DockSpaceId, ImGuiDir.Left, 0.18f, &left, &rest);
        ImGuiP.DockBuilderSplitNode(rest, ImGuiDir.Right, 0.30f, &right, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.26f, &bottom, &main);
        foreach (var name in plan.Left)
            ImGuiP.DockBuilderDockWindow(name, left);
        foreach (var name in plan.Right)
            ImGuiP.DockBuilderDockWindow(name, right);
        foreach (var name in plan.Bottom)
            ImGuiP.DockBuilderDockWindow(name, bottom);
        if (preset == LayoutPreset.FourViewports && plan.Center.Count >= 4)
        {
            uint top, lower, topLeft, topRight, bottomLeft, bottomRight;
            ImGuiP.DockBuilderSplitNode(main, ImGuiDir.Up, 0.5f, &top, &lower);
            ImGuiP.DockBuilderSplitNode(top, ImGuiDir.Left, 0.5f, &topLeft, &topRight);
            ImGuiP.DockBuilderSplitNode(lower, ImGuiDir.Left, 0.5f, &bottomLeft, &bottomRight);
            ImGuiP.DockBuilderDockWindow(plan.Center[0], topLeft);
            ImGuiP.DockBuilderDockWindow(plan.Center[1], topRight);
            ImGuiP.DockBuilderDockWindow(plan.Center[2], bottomLeft);
            ImGuiP.DockBuilderDockWindow(plan.Center[3], bottomRight);
        }
        else
        {
            foreach (var name in plan.Center)
                ImGuiP.DockBuilderDockWindow(name, main);
        }

        ImGuiP.DockBuilderFinish(DockSpaceId);
        LayoutChanged?.Invoke();
    }
}
