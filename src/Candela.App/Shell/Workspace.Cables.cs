using Candela.App.Editing;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Settings;
using Candela.Electrical;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>
/// Cabling in the workspace: the cable tool's state and status messages, the Tools → Cable menu with every cable type,
/// the cable link window opened from a cable click or a connector's cable link, and the editing keys that belong to
/// the viewports' tools — Escape finishes the cable being drawn (or cancels a wall/floor drag, or drops the selected
/// cable node) before it would clear the selection, Backspace removes the last cable point, Delete removes the selected
/// cable node instead of the whole cable.
/// </summary>
internal sealed partial class Workspace
{
    private Action _handleEditingKeys = static () => { };
    private Action<Guid>? _onCableLinkRequested;
    private Action? _onCablesSceneReplaced;
    private string[] _cableMenuLabels = Array.Empty<string>();
    private string _cableMenuLanguage = "";
    private CableCatalog? _cableMenuCatalog;

    /// <summary>The cable link window.</summary>
    internal CableLinksPanel CableLinks => _cableLinks;

    private void InitializeCabling()
    {
        var tool = new CableTool(StagePanelContext.From(_ctx));
        tool.Status += SetStatus;
        _ctx.Tools.Cables = tool;
        _onCableLinkRequested = id =>
        {
            _cableLinks.Show(id);
            FocusPanel(_cableLinks);
        };
        _ctx.Tools.Stage.CableLinkRequested += _onCableLinkRequested;
        _onCablesSceneReplaced = () =>
        {
            tool.Cancel();
            _ctx.Tools.CableNode = null;
        };
        _ctx.Session.SceneReplaced += _onCablesSceneReplaced;
        _handleEditingKeys = HandleEditingKeys;
    }

    private void DisposeCabling()
    {
        if (_onCableLinkRequested is not null)
            _ctx.Tools.Stage.CableLinkRequested -= _onCableLinkRequested;
        if (_onCablesSceneReplaced is not null)
            _ctx.Session.SceneReplaced -= _onCablesSceneReplaced;
    }

    /// <summary>Shows the cable overlay in the active viewport (from the cable link window).</summary>
    private void ShowCableOverlay() => ActiveViewport.View.Overlay = OverlayMode.Cables;

    /// <summary>Tools → Cable: the tool itself (C), then every cable type with its colour.</summary>
    private void DrawCableMenu()
    {
        var tools = _ctx.Tools;
        bool enabled = _ctx.Session.Engine is not null;
        if (!_menu.Begin(Str.ToolCable, Icons.Cable, enabled))
            return;
        Item(EditorActions.ToolCable);
        _menu.Separator();
        var tool = tools.Cables;
        var catalog = tool?.Catalog ?? CableCatalog.Default;
        if (!ReferenceEquals(catalog, _cableMenuCatalog) || _cableMenuLanguage != Loc.Language || _cableMenuLabels.Length != catalog.All.Count)
        {
            _cableMenuCatalog = catalog;
            _cableMenuLanguage = Loc.Language;
            // Room for the colour swatch drawn over the start of the label.
            _cableMenuLabels = catalog.All.Select(t => "  " + CableLoc.CableName(t) + "##" + t.Id).ToArray();
        }

        for (int i = 0; i < catalog.All.Count; i++)
        {
            var type = catalog.All[i];
            bool current = tools.Tool == EditorTool.Cable && tool is not null && string.Equals(tool.CableTypeId, type.Id, StringComparison.OrdinalIgnoreCase);
            bool clicked = _menu.Item(_cableMenuLabels[i], "", current, enabled, type.Id, type.DisplayName);
            if (_menu.IsRealOpen)
            {
                var min = ImGui.GetItemRectMin();
                float h = ImGui.GetTextLineHeight();
                var (r, g, b) = type.ColorSrgb;
                var p = min + new System.Numerics.Vector2(ImGui.GetStyle().ItemSpacing.X * 0.5f, (ImGui.GetItemRectSize().Y - h) * 0.5f + h * 0.3f);
                ImGui.GetWindowDrawList().AddRectFilled(p, p + new System.Numerics.Vector2(h * 1.5f, h * 0.4f), Theme.U32(new System.Numerics.Vector4(r, g, b, 1f)), 2f);
            }

            if (clicked && tool is not null)
            {
                if (tool.IsDrawing && !string.Equals(tool.CableTypeId, type.Id, StringComparison.OrdinalIgnoreCase))
                    tool.Finish();
                tool.CableTypeId = type.Id;
                tools.Tool = EditorTool.Cable;
            }
        }

        _menu.End();
    }

    /// <summary>
    /// Keys of the viewport tools, handled before the shortcuts: Escape ends the cable being drawn, cancels a wall or
    /// floor being drawn or resized, or drops the selected cable node; Backspace removes the cable's last point; Delete
    /// removes the selected cable node. A key used here does not also run its shortcut. A cable still being drawn when
    /// another tool is chosen is finished.
    /// </summary>
    private void HandleEditingKeys()
    {
        var tools = _ctx.Tools;
        var cables = tools.Cables;
        if (tools.Tool != EditorTool.Cable && cables is { IsDrawing: true })
            cables.Finish();
        var io = ImGui.GetIO();
        if (io.WantTextInput || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false) && !_dispatcher.IsConsumed(ImGuiKey.Escape))
        {
            bool handled = false;
            foreach (var viewport in _viewports)
                handled |= viewport.CancelShapeDrawing();
            if (!handled && cables is { IsDrawing: true })
            {
                cables.Finish();
                handled = true;
            }

            if (!handled && tools.CableNode is not null)
            {
                tools.CableNode = null;
                handled = true;
            }

            if (handled)
                _dispatcher.Consume(ImGuiKey.Escape);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Backspace, false) && tools.Tool == EditorTool.Cable && cables is { IsDrawing: true })
        {
            cables.RemoveLastPoint();
            _dispatcher.Consume(ImGuiKey.Backspace);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) && tools.CableNode is { } node && !ViewportBusy())
        {
            var session = _ctx.Session;
            if (CableNodeEdits.Delete(session.Scene, session.Undo, node))
            {
                tools.CableNode = null;
                SetStatus(CableLoc.Get(CableStr.NodeDeleted));
            }

            _dispatcher.Consume(ImGuiKey.Delete);
        }
    }
}
