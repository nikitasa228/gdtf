using System.Numerics;
using Candela.App.Editing;
using Candela.App.Files;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Settings;
using Candela.Core.Editing;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Hexa.NET.ImGui;

namespace Candela.App.Shell;

/// <summary>
/// The library side of the workspace: the user's model library, placing dragged library items into the scene, and the
/// popup that follows a fixture drop (DMX mode, with the address the fixture was patched to).
/// </summary>
internal sealed partial class Workspace : ILibraryHost
{
    private const string PlacementPopupId = "###placement";
    private Guid _placedFixture;
    private Guid _placedImage;
    private FixtureLibraryEntry? _placedEntry;
    private Vector2 _placedAt;
    private bool _openPlacementPopup;
    private LibraryItem? _labelItem;
    private long _labelVersion = -1;
    private string _label = "";
    private Action? _drawPlacementPopup;

    /// <summary>Creates the user's model library (stored in the config folder; read-only in headless runs).</summary>
    private ModelLibrary CreateModelLibrary()
    {
        var models = new ModelLibrary(Path.Combine(ConfigPaths.ConfigDir, "model-library.json"), _ctx.Log) { ReadOnly = _options.IsScreenshotMode };
        models.Load();
        _drawPlacementPopup = DrawPlacementPopup;
        return models;
    }

    /// <inheritdoc/>
    public void ShowModelImport() => _browser.Show(FileBrowserMode.Open, Loc.Get(Str.LibImportTooltip),
        new[]
        {
            new FileFilter(Loc.Get(Str.LibModels), string.Join(";", ModelFilter.Patterns.Concat(ImageFilter.Patterns))),
            Named(ModelFilter, Str.BrowserModels), Named(ImageFilter, Str.BrowserImages),
        },
        StartFolder(), null, path =>
        {
            if (_library.Models.Add(path))
                SetStatus(Loc.Format(Str.LibAdded, Path.GetFileName(path)));
            else if (ModelLibrary.KindOf(path) is null)
                Report(SessionResult.Failed(Loc.Format(Str.MsgDropUnsupported, Path.GetFileName(path))));
        });

    /// <inheritdoc/>
    public void ShowGdtfImport() => ShowImport(ImportKind.Gdtf);

    // ---- IViewportHost: library drops -----------------------------------------------------------------------------

    /// <inheritdoc/>
    public LibraryItem? DraggedLibraryItem => _library.Dragged;

    /// <inheritdoc/>
    public FixtureInfo? FixtureInfoOf(FixtureLibraryEntry entry) => _library.FixtureInfo?.Get(entry);

    /// <inheritdoc/>
    public string DropLabel(LibraryItem item)
    {
        // Built once per drag (and again when the scene changed): name, default mode and the address it will get.
        var session = _ctx.Session;
        long version = session.Scene.Version;
        if (ReferenceEquals(item, _labelItem) && version == _labelVersion && _label.Length > 0)
            return _label;
        _labelItem = item;
        _labelVersion = version;
        if (item.Fixture is { } entry && session.Engine is { } engine)
        {
            var info = FixtureInfoOf(entry);
            if (info?.DefaultMode is not { } mode)
            {
                _labelVersion = -1; // modes still loading: try again next frame
                return _label = entry.ModelName;
            }

            var addresses = AutoPatch.FindFree(AutoPatch.UsedRanges(session.Scene, engine.Profiles), mode.Breaks);
            string patch = addresses is { Count: > 0 } a ? string.Join(" + ", a.Select(x => x.ToString())) : Loc.Get(Str.PlaceUnpatched);
            return _label = $"{entry.ModelName} · {mode.Name} · {Loc.Format(Str.PlaceChannels, mode.Footprint)} · {patch}";
        }

        return _label = item.Name;
    }

    /// <inheritdoc/>
    public void PlaceLibraryItem(LibraryItem item, SurfaceHit hit, Vector2 screen)
    {
        ArgumentNullException.ThrowIfNull(item);
        var session = _ctx.Session;
        if (item.Fixture is { } entry)
        {
            if (session.Engine is null)
                return;
            // The modes are normally read while dragging; a very quick drop waits briefly for them.
            var info = _library.FixtureInfo?.Get(entry) ?? _library.FixtureInfo?.GetNow(entry, TimeSpan.FromSeconds(3));
            var entity = LibraryPlacement.PlaceFixture(session, entry, info, hit);
            var fixture = entity.Get<FixtureComponent>();
            string patch = fixture is null || fixture.Addresses.Count == 0 ? Loc.Get(Str.PlaceUnpatched) : LibraryPlacement.AddressText(fixture);
            SetStatus(Loc.Format(Str.MsgPlaced, entity.Name, patch));
            _ctx.Log.Info("Library", $"Placed '{entry.FileName}' as '{entity.Name}' ({fixture?.GdtfMode}, {patch}) at {hit.Position}.");
            if (info is { Modes.Count: > 1 })
            {
                _placedFixture = entity.Id;
                _placedImage = Guid.Empty;
                _placedEntry = entry;
                _placedAt = screen;
                _openPlacementPopup = true;
            }

            return;
        }

        if (item.Model is { } model)
        {
            var kind = model.Kind == ModelItemKind.Image ? ImportKind.Image : ImportKind.Model;
            ImportFile(model.Path, kind, PlacementFor(kind, hit));
        }
    }

    /// <summary>Opens the popup that lets the user choose between reference drawing and poster for an image just placed.</summary>
    private void ShowImagePopup(Guid image, Vector2 screen)
    {
        _placedImage = image;
        _placedFixture = Guid.Empty;
        _placedEntry = null;
        _placedAt = screen;
        _openPlacementPopup = true;
    }

    /// <summary>Draws the popup that lets the user pick the DMX mode of a fixture (or the kind of an image) just dropped.</summary>
    private void DrawPlacementPopupGuarded()
    {
        if (_drawPlacementPopup is not null && !_guard.Run("placement popup", _drawPlacementPopup))
            _openPlacementPopup = false;
    }

    private void DrawPlacementPopup()
    {
        if (_openPlacementPopup)
        {
            _openPlacementPopup = false;
            ImGui.SetNextWindowPos(_placedAt + new Vector2(12f, 12f), ImGuiCond.Always);
            ImGui.OpenPopup(PlacementPopupId);
        }

        if (!ImGui.BeginPopup(PlacementPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return;
        var session = _ctx.Session;
        if (_placedImage != Guid.Empty)
        {
            DrawImagePlacement(session);
            ImGui.EndPopup();
            return;
        }

        var entity = session.Scene.Find(_placedFixture);
        var fixture = entity?.Get<FixtureComponent>();
        var info = _placedEntry is { } entry ? _library.FixtureInfo?.Get(entry) : null;
        if (entity is null || fixture is null || info is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextColored(Theme.Accent, $"{Icons.Lightbulb}  {entity.Name}");
        ImGui.Separator();
        ImGui.TextDisabled(Loc.Get(Str.PlaceMode));
        float scale = _frameScale;
        ImGui.SetNextItemWidth(320f * scale);
        string current = fixture.GdtfMode ?? info.DefaultMode?.Name ?? "";
        if (ImGui.BeginCombo("##mode", $"{current}  ·  {Loc.Format(Str.PlaceChannels, info.Find(current)?.Footprint ?? 0)}"))
        {
            for (int i = 0; i < info.Modes.Count; i++)
            {
                var mode = info.Modes[i];
                bool selected = string.Equals(mode.Name, current, StringComparison.Ordinal);
                if (ImGui.Selectable($"{mode.Name}  ·  {Loc.Format(Str.PlaceChannels, mode.Footprint)}##m{i}", selected))
                    LibraryPlacement.ChangeMode(session, entity, mode);
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(Loc.Get(Str.PlaceAddress));
        ImGui.TextUnformatted(fixture.Addresses.Count == 0 ? Loc.Get(Str.PlaceUnpatched) : LibraryPlacement.AddressText(fixture));
        ImGui.Spacing();
        if (ImGui.Button(Loc.Get(Str.MsgOk), new Vector2(110f * scale, 0f)) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawImagePlacement(EditorSession session)
    {
        var entity = session.Scene.Find(_placedImage);
        if (entity?.Get<ImageComponent>() is not { } image)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.TextColored(Theme.Accent, $"{Icons.Image}  {entity.Name}");
        ImGui.Separator();
        ImGui.TextDisabled(Loc.Get(Str.PlaceImageMode));
        if (ImGui.RadioButton(Loc.Get(Str.PlaceReference), image.Mode == ImageMode.Reference) && image.Mode != ImageMode.Reference)
            session.Undo.Execute(ModifyComponentCommand.Create(image, i => i.Mode = ImageMode.Reference, Loc.English_(Str.PlaceReference)));
        if (ImGui.RadioButton(Loc.Get(Str.PlacePoster), image.Mode == ImageMode.Poster) && image.Mode != ImageMode.Poster)
        {
            // A poster is a printed surface: opaque, lit by the stage light.
            session.Undo.Execute(ModifyComponentCommand.Create(image, i =>
            {
                i.Mode = ImageMode.Poster;
                i.Opacity = 1f;
            }, Loc.English_(Str.PlacePoster)));
        }

        ImGui.Spacing();
        if (ImGui.Button(Loc.Get(Str.MsgOk), new Vector2(110f * _frameScale, 0f)) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
            ImGui.CloseCurrentPopup();
    }

    /// <summary>The fixture whose mode popup is open (tests and scripts), or empty.</summary>
    internal Guid PlacementPopupFixture => ImGui.IsPopupOpen(PlacementPopupId) ? _placedFixture : Guid.Empty;
}
