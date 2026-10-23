using System.Numerics;
using Candela.App.Docking;
using Candela.App.Editing;
using Candela.App.Files;
using Candela.App.Input;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.App.Windows;
using Candela.Core.Settings;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>Commands and the main menu of the workspace.</summary>
internal sealed partial class Workspace
{
    private static readonly FileFilter ProjectFilter = new("Candela", "*.candela");
    private static readonly FileFilter MvrFilter = new("MVR", "*.mvr");
    private static readonly FileFilter ModelFilter = new("3D", "*.obj;*.fbx;*.dae;*.3ds;*.gltf;*.glb;*.stl;*.ply");
    private static readonly FileFilter ImageFilter = new("Images", "*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.hdr");

    private void RegisterCommands()
    {
        var session = _ctx.Session;
        bool HasSelection() => !session.Selection.IsEmpty;

        // File
        Command(EditorActions.FileNew, Str.FileNew, Icons.FilePlus, NewScene);
        Command(EditorActions.FileOpen, Str.FileOpen, Icons.FolderOpen, ShowOpen);
        Command(EditorActions.FileSave, Str.FileSave, Icons.Save, Save);
        Command(EditorActions.FileSaveAs, Str.FileSaveAs, Icons.Save, ShowSaveAs);
        Command(EditorActions.FileImport, Str.FileImport, Icons.FileInput, () => ShowImport(null));
        Command(EditorActions.FileExport, Str.FileExport, Icons.FileOutput, ShowExport);
        // F12 / File → Render: brings the Render panel to the front and starts a render with its current settings (a
        // running render goes on; the panel shows its progress).
        Command(EditorActions.Render, Str.FileRender, Icons.Clapperboard, StartRender);
        Command(EditorActions.Settings, Str.FileSettings, Icons.Settings, () => _settingsWindow.Open());
        Command(AppActions.Exit, Str.FileExit, Icons.X, () => _ctx.Window.SetShouldClose(true));

        // Edit
        // Not while a gizmo or navigation drag is under way: the drag would re-apply its transform at once and the undone
        // step (and the redo history) would be lost.
        Command(EditorActions.Undo, Str.EditUndo, Icons.Undo, session.Undo.Undo, () => session.Undo.CanUndo && !ViewportBusy());
        Command(EditorActions.Redo, Str.EditRedo, Icons.Redo, session.Undo.Redo, () => session.Undo.CanRedo && !ViewportBusy());
        Command(AppActions.Cut, Str.EditCut, Icons.Scissors, () => Notify(Count(_edit.Cut(session.Scene, session.Selection, session.Undo), Str.EditCut)), HasSelection);
        Command(EditorActions.Copy, Str.EditCopy, Icons.Copy, () => Notify(Count(_edit.Copy(session.Scene, session.Selection), Str.EditCopy)), HasSelection);
        Command(EditorActions.Paste, Str.EditPaste, Icons.Clipboard, () => AfterCopy(_edit.Paste(session.Scene, session.Selection, session.Undo)), () => _edit.CanPaste);
        Command(EditorActions.Duplicate, Str.EditDuplicate, Icons.Copy, () => AfterCopy(EditOperations.Duplicate(session.Scene, session.Selection, session.Undo)), HasSelection);
        Command(EditorActions.Delete, Str.EditDelete, Icons.Trash, () => EditOperations.Delete(session.Scene, session.Selection, session.Undo), HasSelection);
        Command(EditorActions.SelectAll, Str.EditSelectAll, Icons.BoxSelect, () => SelectionController.SelectAll(session.Scene, session.Selection));
        Command(EditorActions.DeselectAll, Str.EditDeselect, Icons.X, session.Selection.Clear, HasSelection);

        // Tools
        var tools = _ctx.Tools;
        Command(EditorActions.ToolSelect, Str.ToolSelect, Icons.Pointer, () => tools.Tool = EditorTool.Select, null, () => tools.Tool == EditorTool.Select);
        Command(EditorActions.ToolMove, Str.ToolMove, Icons.Move, () => tools.Tool = EditorTool.Move, null, () => tools.Tool == EditorTool.Move);
        Command(EditorActions.ToolRotate, Str.ToolRotate, Icons.Rotate3D, () => tools.Tool = EditorTool.Rotate, null, () => tools.Tool == EditorTool.Rotate);
        Command(AppActions.ToggleSnap, Str.ToolSnapping, Icons.Magnet, () => tools.Snap = !tools.Snap, null, () => tools.Snap);
        Command(AppActions.ToggleSpace, Str.ToolSpaceLocal, Icons.Axis3D, () => tools.LocalSpace = !tools.LocalSpace, null, () => tools.LocalSpace);
        Command(EditorActions.ToolWall, Str.ToolWall, Icons.BrickWall, () => tools.Tool = EditorTool.Wall, () => session.Engine is not null,
            () => tools.Tool == EditorTool.Wall);
        Command(AppActions.ToolFloor, Str.ToolFloor, Icons.Square, () => tools.Tool = EditorTool.Floor, () => session.Engine is not null,
            () => tools.Tool == EditorTool.Floor);
        Command(EditorActions.ToolCable, Str.ToolCable, Icons.Cable, () => tools.Tool = EditorTool.Cable, () => session.Engine is not null,
            () => tools.Tool == EditorTool.Cable);

        // View (active viewport)
        Command(EditorActions.FrameSelection, Str.ViewFrameSelection, Icons.Focus, () => ActiveViewport.FrameSelection());
        Command(EditorActions.ViewToggleOrtho, Str.ViewToggleProjection, Icons.Camera, () => ActiveViewport.Camera.ToggleProjection());
        Command(EditorActions.ViewTop, Str.ViewTop, Icons.ArrowUp, () => ActiveViewport.Camera.SnapTo(ViewDirection.Top));
        Command(EditorActions.ViewFront, Str.ViewFront, Icons.ArrowRight, () => ActiveViewport.Camera.SnapTo(ViewDirection.Front));
        Command(EditorActions.ViewRight, Str.ViewRight, Icons.ArrowLeft, () => ActiveViewport.Camera.SnapTo(ViewDirection.Right));
        Command(AppActions.ViewLeft, Str.ViewLeft, Icons.ArrowRight, () => ActiveViewport.Camera.SnapTo(ViewDirection.Left));
        Command(AppActions.ViewBack, Str.ViewBack, Icons.ArrowLeft, () => ActiveViewport.Camera.SnapTo(ViewDirection.Back));
        Command(AppActions.ViewBottom, Str.ViewBottom, Icons.ArrowUp, () => ActiveViewport.Camera.SnapTo(ViewDirection.Bottom));
        Command(EditorActions.ShadingWireframe, Str.ShadingWireframe, Icons.Frame, () => ActiveViewport.View.Shading = ViewShading.Wireframe, null,
            () => ActiveViewport.View.Shading == ViewShading.Wireframe);
        Command(EditorActions.ShadingColorless, Str.ShadingColorless, Icons.Box, () => ActiveViewport.View.Shading = ViewShading.Colorless, null,
            () => ActiveViewport.View.Shading == ViewShading.Colorless);
        Command(EditorActions.ShadingFull, Str.ShadingFull, Icons.Sparkles, () => ActiveViewport.View.Shading = ViewShading.Full, null,
            () => ActiveViewport.View.Shading == ViewShading.Full);
        Command(AppActions.OverlayNone, Str.OverlayNone, Icons.Layers, () => ActiveViewport.View.Overlay = OverlayMode.None, null,
            () => ActiveViewport.View.Overlay == OverlayMode.None);
        Command(EditorActions.OverlayLux, Str.OverlayLux, Icons.Sun, () => ActiveViewport.View.Overlay = OverlayMode.Lux, null,
            () => ActiveViewport.View.Overlay == OverlayMode.Lux);
        Command(EditorActions.OverlayNoise, Str.OverlayNoise, Icons.AudioLines, () => ActiveViewport.View.Overlay = OverlayMode.Noise, null,
            () => ActiveViewport.View.Overlay == OverlayMode.Noise);
        Command(EditorActions.OverlayCables, Str.OverlayCables, Icons.Cable, () => ActiveViewport.View.Overlay = OverlayMode.Cables, null,
            () => ActiveViewport.View.Overlay == OverlayMode.Cables);
        Command(AppActions.OverlaySmoke, Str.OverlaySmoke, Icons.CloudFog, () => ActiveViewport.View.Overlay = OverlayMode.Smoke, null,
            () => ActiveViewport.View.Overlay == OverlayMode.Smoke);
        Command(AppActions.ToggleGrid, Str.ViewShowGrid, Icons.Grid, () => ActiveViewport.View.ShowGrid = !ActiveViewport.View.ShowGrid, null,
            () => ActiveViewport.View.ShowGrid);

        // Window / help
        Command(AppActions.NewViewport, Str.ViewNewViewport, Icons.Plus, () => NewViewportNextToActive(), () => _viewports.Count(v => v.IsOpen) < MaxViewports);
        Command(AppActions.ResetLayout, Str.ViewResetLayout, Icons.RotateCcw, () => ApplyPreset(LayoutPreset.Default));
        Command(AppActions.Shortcuts, Str.HelpShortcuts, Icons.Keyboard, () => _shortcuts.IsOpen = true);
    }

    private void Command(string id, Str label, string icon, Action run, Func<bool>? enabled = null, Func<bool>? isChecked = null) =>
        _commands.Register(new EditorCommand(id, label, icon, run, enabled, isChecked));

    private static string Count(int n, Str what) => $"{Loc.Get(what)}: {n}";

    // ---- file actions -------------------------------------------------------------------------------------------

    private void ShowOpen() => _browser.Show(FileBrowserMode.Open, Loc.Get(Str.BrowserOpenTitle),
        new[] { new FileFilter(Loc.Get(Str.BrowserScenes), "*.candela;*.mvr"), Named(ProjectFilter, Str.BrowserCandelaProjects), Named(MvrFilter, Str.BrowserMvr), AllFiles() },
        StartFolder(), null, OpenPath);

    private void OpenPath(string path) => OpenFile(path);

    private void Save() => SaveThen(null);

    private void ShowSaveAs() => ShowSaveAs(null);

    private void ShowImport(ImportKind? kind)
    {
        FileFilter[] filters = kind switch
        {
            ImportKind.Project => new[] { Named(ProjectFilter, Str.BrowserCandelaProjects) },
            ImportKind.Model => new[] { Named(ModelFilter, Str.BrowserModels) },
            ImportKind.Mvr => new[] { Named(MvrFilter, Str.BrowserMvr) },
            ImportKind.Image => new[] { Named(ImageFilter, Str.BrowserImages) },
            ImportKind.Gdtf => new[] { Named(GdtfFilter, Str.BrowserGdtf) },
            _ => new[]
            {
                new FileFilter(Loc.Get(Str.FileImport), string.Join(";", ProjectFilter.Patterns.Concat(MvrFilter.Patterns).Concat(ModelFilter.Patterns).Concat(ImageFilter.Patterns).Concat(GdtfFilter.Patterns))),
                Named(ProjectFilter, Str.BrowserCandelaProjects), Named(MvrFilter, Str.BrowserMvr), Named(ModelFilter, Str.BrowserModels), Named(ImageFilter, Str.BrowserImages),
                Named(GdtfFilter, Str.BrowserGdtf),
            },
        };
        _browser.Show(FileBrowserMode.Open, Loc.Get(Str.BrowserImportTitle), filters, StartFolder(), null,
            path => ImportFile(path, kind ?? KindOf(path)));
    }

    /// <summary>The import kind a file name implies (unknown extensions are treated as 3D models).</summary>
    internal static ImportKind KindOf(string path)
    {
        string name = Path.GetFileName(path);
        if (ProjectFilter.Matches(name))
            return ImportKind.Project;
        if (MvrFilter.Matches(name))
            return ImportKind.Mvr;
        if (ImageFilter.Matches(name))
            return ImportKind.Image;
        if (GdtfFilter.Matches(name))
            return ImportKind.Gdtf;
        return ImportKind.Model;
    }

    private void ShowExport() => _browser.Show(FileBrowserMode.Save, Loc.Get(Str.BrowserExportTitle), new[] { Named(MvrFilter, Str.BrowserMvr) },
        StartFolder(), SuggestedName() + ".mvr", ExportMvr);

    private string? StartFolder() => (_ctx.Session.FilePath ?? _ctx.Session.SourcePath) is { } p ? Path.GetDirectoryName(p) : null;

    private static FileFilter Named(FileFilter f, Str name) => new(Loc.Get(name), f.Patterns.ToArray());

    private static FileFilter AllFiles() => new(Loc.Get(Str.BrowserAllFiles), "*");

    // ---- main menu ----------------------------------------------------------------------------------------------

    private void DrawMainMenu()
    {
        if (!ImGui.BeginMainMenuBar())
            return;
        _menu.BeginBar();
        if (_menu.Begin(Str.MenuFile))
        {
            Item(EditorActions.FileNew);
            Item(EditorActions.FileOpen);
            if (_menu.Begin(Str.FileOpenRecent, Icons.History))
            {
                var recent = _ctx.Settings.Settings.RecentFiles;
                if (recent.Count == 0)
                    _menu.Item(Loc.Get(Str.FileNoRecent), "", false, false);
                for (int i = 0; i < recent.Count; i++)
                {
                    string path = recent[i];
                    if (_menu.Item($"{Icons.File}  {SafeText.Label(Path.GetFileName(path))}##{i}", "", false, true, path))
                        OpenPath(path);
                }

                _menu.Separator();
                if (_menu.Item(Str.FileClearRecent, Icons.Trash, "", false, recent.Count > 0))
                {
                    recent.Clear();
                    _ctx.Settings.RequestSave(_ctx.Timer.Elapsed);
                }

                _menu.End();
            }

            _menu.Separator();
            Item(EditorActions.FileSave);
            Item(EditorActions.FileSaveAs);
            _menu.Separator();
            if (_menu.Begin(Str.FileImport, Icons.FileInput))
            {
                if (_menu.Item(Str.FileImportProject, Icons.Package))
                    ShowImport(ImportKind.Project);
                if (_menu.Item(Str.FileImportModel, Icons.Box))
                    ShowImport(ImportKind.Model);
                if (_menu.Item(Str.FileImportMvr, Icons.Theater))
                    ShowImport(ImportKind.Mvr);
                if (_menu.Item(Str.FileImportImage, Icons.Image))
                    ShowImport(ImportKind.Image);
                if (_menu.Item(Str.FileImportGdtf, Icons.Lightbulb))
                    ShowImport(ImportKind.Gdtf);
                _menu.End();
            }

            if (_menu.Begin(Str.FileExport, Icons.FileOutput))
            {
                if (_menu.Item(Str.FileExportMvr, Icons.Theater, ChordText(_keys, EditorActions.FileExport)))
                    ShowExport();
                _menu.End();
            }

            _menu.Separator();
            Item(EditorActions.Render);
            _menu.Separator();
            Item(EditorActions.Settings);
            _menu.Separator();
            Item(AppActions.Exit);
            _menu.End();
        }

        if (_menu.Begin(Str.MenuEdit))
        {
            var undo = _ctx.Session.Undo;
            UndoItem(EditorActions.Undo, Str.EditUndo, undo.UndoDescription);
            UndoItem(EditorActions.Redo, Str.EditRedo, undo.RedoDescription);
            _menu.Separator();
            Item(AppActions.Cut);
            Item(EditorActions.Copy);
            Item(EditorActions.Paste);
            Item(EditorActions.Duplicate);
            Item(EditorActions.Delete);
            _menu.Separator();
            Item(EditorActions.SelectAll);
            Item(EditorActions.DeselectAll);
            _menu.End();
        }

        if (_menu.Begin(Str.MenuView))
        {
            var viewport = ActiveViewport;
            if (_menu.Begin(Str.ViewShading, Icons.Sparkles))
            {
                Item(EditorActions.ShadingWireframe);
                Item(EditorActions.ShadingColorless);
                Item(EditorActions.ShadingFull);
                _menu.End();
            }

            if (_menu.Begin(Str.ViewProjection, Icons.Camera))
            {
                bool persp = viewport.Camera.Projection == ProjectionKind.Perspective;
                if (_menu.Item(Str.ProjectionPerspective, "", "", persp) && !persp)
                    viewport.Camera.ToggleProjection();
                if (_menu.Item(Str.ProjectionOrthographic, "", ChordText(_keys, EditorActions.ViewToggleOrtho), !persp) && persp)
                    viewport.Camera.ToggleProjection();
                _menu.End();
            }

            if (_menu.Begin(Str.ViewCameraViews, Icons.Axis3D))
            {
                Item(EditorActions.ViewTop);
                Item(EditorActions.ViewFront);
                Item(EditorActions.ViewRight);
                Item(AppActions.ViewLeft);
                Item(AppActions.ViewBack);
                Item(AppActions.ViewBottom);
                _menu.End();
            }

            if (_menu.Begin(Str.ViewOverlay, Icons.Layers))
            {
                Item(AppActions.OverlayNone);
                Item(EditorActions.OverlayLux);
                Item(EditorActions.OverlayNoise);
                Item(EditorActions.OverlayCables);
                Item(AppActions.OverlaySmoke);
                _menu.End();
            }

            if (_menu.Begin(Str.ViewWorkLight, Icons.Lightbulb, _ctx.Session.Engine is not null))
            {
                WorkLightItem(SessionActions.WorkLightOff, Str.WorkLightOff, Str.WorkLightOffHint);
                WorkLightItem(SessionActions.WorkLightAuto, Str.WorkLightAuto, Str.WorkLightAutoHint);
                WorkLightItem(SessionActions.WorkLightOn, Str.WorkLightOn, Str.WorkLightOnHint);
                _menu.End();
            }

            _menu.Separator();
            Item(AppActions.ToggleGrid);
            if (_menu.Item(Str.ViewShowStats, Icons.Activity, "", viewport.ShowStats))
                viewport.ShowStats = !viewport.ShowStats;
            if (_menu.Item(Str.ViewShowViewCube, Icons.Box, "", viewport.ShowViewCube))
                viewport.ShowViewCube = !viewport.ShowViewCube;
            _menu.Separator();
            Item(EditorActions.FrameSelection);
            _menu.Separator();
            Item(AppActions.NewViewport);
            Item(AppActions.ResetLayout);
            _menu.End();
        }

        if (_menu.Begin(Str.MenuTools))
        {
            Item(EditorActions.ToolSelect);
            Item(EditorActions.ToolMove);
            Item(EditorActions.ToolRotate);
            _menu.Separator();
            Item(AppActions.ToggleSnap);
            Item(AppActions.ToggleSpace);
            _menu.Separator();
            Item(EditorActions.ToolWall);
            Item(AppActions.ToolFloor);
            DrawCableMenu();
            _menu.Separator();
            Item(SessionActions.OpenWhite);
            Item(SessionActions.PauseSimulation);
            _menu.End();
        }

        if (_menu.Begin(Str.MenuWindow))
        {
            foreach (var panel in _panels)
            {
                if (_menu.Item(panel.MenuLabel, "", panel.IsOpen, true, panel.Id))
                    TogglePanel(panel);
            }

            _menu.Separator();
            Item(AppActions.NewViewport);
            _menu.Separator();
            if (_menu.Begin(Str.WindowLayouts, Icons.LayoutGrid))
            {
                if (_menu.Item(Str.WindowLayoutDefault, Icons.LayoutPanelLeft))
                    ApplyPreset(LayoutPreset.Default);
                if (_menu.Item(Str.WindowLayoutFourViews, Icons.LayoutGrid))
                    ApplyPreset(LayoutPreset.FourViewports);
                var saved = SavedLayouts();
                if (saved.Count > 0)
                    _menu.Separator();
                foreach (var name in saved)
                    if (_menu.Item($"{Icons.Panels}  {name}", "", false, true, name))
                        ApplyLayout(name);
                _menu.Separator();
                if (_menu.Item(Str.WindowSaveLayoutAs, Icons.Save))
                    _messages.Prompt(Loc.Get(Str.WindowSaveLayoutAs), Loc.Get(Str.MsgLayoutNameHint), SaveLayoutAs);
                if (saved.Count > 0 && _menu.Begin(Str.WindowDeleteLayout, Icons.Trash))
                {
                    foreach (var name in saved)
                    {
                        string n = name;
                        if (_menu.Item(name, "", false, true, name))
                            _messages.Ask(Loc.Get(Str.WindowDeleteLayout), Loc.Format(Str.MsgDeleteLayoutQuestion, n), yes =>
                            {
                                if (yes)
                                {
                                    _layouts.Delete(n);
                                    _savedLayouts = null;
                                }
                            });
                    }

                    _menu.End();
                }

                _menu.End();
            }

            Item(AppActions.ResetLayout);
            _menu.End();
        }

        if (_menu.Begin(Str.MenuHelp))
        {
            Item(AppActions.Shortcuts);
            if (_menu.Item(Str.HelpDocs, Icons.BookOpen))
                OpenDocsFolder();
            DrawLibrarySetupMenuItem();
            _menu.Separator();
            if (_menu.Item(Str.HelpAbout, Icons.Info))
                _about.IsOpen = true;
            _menu.End();
        }

        if (_menu.EndBar() is { } missed)
            ReportMissingMenu(missed);
        ImGui.EndMainMenuBar();
    }

    /// <summary>A menu item for a registered command (label, icon, shortcut, check mark, enabled state).</summary>
    private void Item(string commandId)
    {
        if (_commands.Find(commandId) is not { } command)
            return;
        bool isChecked = command.IsChecked?.Invoke() ?? false;
        string label = command.Icon.Length > 0 ? $"{command.Icon}  {command.Text}" : command.Text;
        if (_menu.Item(label, ChordText(_keys, commandId), isChecked, command.Enabled, command.Label.ToString(), Loc.English_(command.Label)))
            command.Run();
    }

    /// <summary>A work light mode in View → Work Light: the short label, the command's shortcut, its check mark and a hint.</summary>
    private void WorkLightItem(string commandId, Str label, Str hint)
    {
        if (_commands.Find(commandId) is not { } command)
            return;
        bool isChecked = command.IsChecked?.Invoke() ?? false;
        if (_menu.Item(label, "", ChordText(_keys, commandId), isChecked, command.Enabled))
            command.Run();
        if (_menu.IsRealOpen && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            SafeText.Tooltip(Loc.Get(hint));
    }

    private void UndoItem(string commandId, Str label, string? description)
    {
        if (_commands.Find(commandId) is not { } command)
            return;
        string text = description is null ? Loc.Get(label) : $"{Loc.Get(label)} “{description}”";
        if (_menu.Item($"{command.Icon}  {text}", ChordText(_keys, commandId), false, command.Enabled, label.ToString(), Loc.English_(label)))
            command.Run();
    }
}
