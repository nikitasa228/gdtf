using System.Globalization;
using System.Numerics;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Assets;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The wall/floor and model part of the properties panel: dimensions (walls and floors: width, height or depth,
/// thickness; models: the size of their bounding box at the current scale) and the built-in materials of
/// <see cref="MaterialLibrary"/> with a lit preview sphere — for walls the surface material and its texture scale, for
/// models one override per material slot (or the model's own material) and shadow casting. With several objects
/// selected the fields edit all of them at once (one undo step); fields whose values differ are marked.
/// </summary>
internal sealed class SurfaceInspectorView
{
    private readonly StagePanelContext _stage;
    private readonly List<WallComponent> _walls = new();
    private readonly List<MeshComponent> _meshes = new();
    private long _selectionKey = -1;
    private long _sceneVersion = -1;
    private Guid _modelFor;
    private string _modelSize = "";
    private string[] _slotNames = Array.Empty<string>();
    private string _formatsLanguage = "";
    private string _metres2 = "%.2f m";
    private string _metres3 = "%.3f m";
    private float _areaValue = float.NaN;
    private string _areaText = "";

    /// <summary>Creates the view.</summary>
    public SurfaceInspectorView(StagePanelContext stage)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
    }

    /// <summary>The selected walls (primary first), refreshed by <see cref="Draw"/>.</summary>
    internal IReadOnlyList<WallComponent> Walls => _walls;

    /// <summary>The selected models (primary first).</summary>
    internal IReadOnlyList<MeshComponent> Meshes => _meshes;

    /// <summary>Draws the surface and model sections for the selection whose primary object is <paramref name="entity"/>.</summary>
    public void Draw(Entity entity, float labelWidth)
    {
        Collect(entity);
        if (_walls.Count > 0 && ImGui.CollapsingHeader(StageLoc.With(Icons.BrickWall, StageStr.PropsSurface), ImGuiTreeNodeFlags.DefaultOpen))
            DrawWalls(labelWidth);
        if (_meshes.Count > 0 && ImGui.CollapsingHeader(StageLoc.With(Icons.Box, StageStr.PropsModel), ImGuiTreeNodeFlags.DefaultOpen))
            DrawModels(entity, labelWidth);
    }

    /// <summary>Collects the walls and models of the selection (primary first) when the selection or the scene changed.</summary>
    internal void Collect(Entity primary)
    {
        var scene = _stage.Scene;
        var ids = _stage.Selection.Ids;
        long key = ids.Count;
        foreach (var id in ids)
            key = key * 31 + id.GetHashCode();
        key ^= primary.Id.GetHashCode();
        if (key == _selectionKey && scene.Version == _sceneVersion)
            return;
        _selectionKey = key;
        _sceneVersion = scene.Version;
        _walls.Clear();
        _meshes.Clear();
        Add(primary);
        foreach (var id in ids)
        {
            if (id != primary.Id && scene.Find(id) is { } e)
                Add(e);
        }

        void Add(Entity e)
        {
            if (e.Get<WallComponent>() is { } wall)
                _walls.Add(wall);
            if (e.Get<MeshComponent>() is { } mesh)
                _meshes.Add(mesh);
        }
    }

    private void DrawWalls(float labelWidth)
    {
        var first = _walls[0];
        if (_walls.Count > 1)
            Muted(StageLoc.Format(StageStr.PropsEditsAll, _walls.Count));
        if (!PropertyGrid.Begin("##walls", labelWidth))
            return;
        bool floor = first.Kind != WallKind.Wall;
        RefreshFormats();
        PropertyGrid.Row(StageLoc.Get(StageStr.SurfaceKind));
        ImGui.TextUnformatted(StageLoc.Get(first.Kind switch
        {
            WallKind.Floor => StageStr.SurfaceFloor,
            WallKind.Wall => StageStr.SurfaceWall,
            _ => StageStr.SurfaceCeiling,
        }));
        FloatRow(StageLoc.Get(StageStr.SurfaceWidth), "width", static w => w.Width, static (w, v) => w.Width = v, 0.01f, 0.01f, 1000f, _metres2);
        FloatRow(StageLoc.Get(floor ? StageStr.SurfaceDepth : StageStr.SurfaceHeight), "height", static w => w.Height, static (w, v) => w.Height = v,
            0.01f, 0.01f, 1000f, _metres2);
        FloatRow(StageLoc.Get(StageStr.SurfaceThickness), "thickness", static w => w.Thickness, static (w, v) => w.Thickness = v, 0.001f, 0.001f,
            10f, _metres3);
        PropertyGrid.Row(StageLoc.Get(StageStr.SurfaceArea));
        ImGui.TextUnformatted(AreaText(first.Width * first.Height));
        PropertyGrid.Row(Loc.Get(Str.PropMaterial));
        string current = first.MaterialId;
        bool mixed = false;
        foreach (var w in _walls)
            mixed |= w.MaterialId != current;
        if (MaterialCombo("##wall-material", current, mixed, allowOwn: false, ownName: null, out string? chosen) && chosen is not null)
        {
            _stage.Undo.Execute(new SetFieldCommand<WallComponent, string>(_walls, "material", static w => w.MaterialId,
                static (w, v) => w.MaterialId = v, chosen, StageLoc.Format(StageStr.UndoMaterial, PropertiesPanel.MaterialName(chosen))));
        }

        FloatRow(StageLoc.Get(StageStr.SurfaceUvScale), "uv", static w => w.UvScale, static (w, v) => w.UvScale = v, 0.01f, 0.05f, 50f, "%.2f×");
        PropertyGrid.End();
        MaterialPreview(current);
    }

    private void FloatRow(string label, string field, Func<WallComponent, float> get, Action<WallComponent, float> set, float speed, float min,
        float max, string format)
    {
        PropertyGrid.Row(label);
        float value = get(_walls[0]);
        bool mixed = false;
        for (int i = 1; i < _walls.Count; i++)
            mixed |= MathF.Abs(get(_walls[i]) - value) > 1e-5f;
        if (mixed)
            MixedMarker();
        ImGui.PushID(field);
        if (ImGui.DragFloat("##v", ref value, speed, min, max, format) && float.IsFinite(value))
        {
            _stage.Undo.Execute(new SetFieldCommand<WallComponent, float>(_walls, field, get, set, Math.Clamp(value, min, max),
                StageLoc.Format(StageStr.UndoDimension, label)));
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            _stage.Undo.BreakMerge();
        if (mixed && ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(StageStr.PropsMixed));
        ImGui.PopID();
    }

    private void DrawModels(Entity entity, float labelWidth)
    {
        RefreshModel(entity);
        if (_meshes.Count > 1)
            Muted(StageLoc.Format(StageStr.PropsEditsAll, _meshes.Count));
        if (!PropertyGrid.Begin("##models", labelWidth))
            return;
        PropertyGrid.Row(StageLoc.Get(StageStr.ModelSize));
        ImGui.TextUnformatted(_modelSize);
        var first = _meshes[0];
        int slots = Math.Max(1, _slotNames.Length);
        for (int slot = 0; slot < slots && slot < 16; slot++)
        {
            ImGui.PushID(slot);
            string? ownName = slot < _slotNames.Length ? _slotNames[slot] : null;
            PropertyGrid.Row(slots > 1 ? StageLoc.Format(StageStr.ModelSlot, slot + 1) : Loc.Get(Str.PropMaterial));
            string? current = SlotMaterial(first, slot);
            bool mixed = false;
            foreach (var m in _meshes)
                mixed |= SlotMaterial(m, slot) != current;
            if (MaterialCombo("##slot", current, mixed, allowOwn: true, ownName, out string? chosen))
            {
                int s = slot;
                _stage.Undo.Execute(new SetFieldCommand<MeshComponent, string?>(_meshes, "material-" + s.ToString(CultureInfo.InvariantCulture),
                    m => m.MaterialOverrides is { } mo && mo.TryGetValue(s, out var v) ? v : null,
                    (m, v) =>
                    {
                        m.MaterialOverrides ??= new Dictionary<int, string>();
                        if (v is null)
                            m.MaterialOverrides.Remove(s);
                        else
                            m.MaterialOverrides[s] = v;
                    },
                    chosen, StageLoc.Format(StageStr.UndoMaterial, chosen is null ? StageLoc.Get(StageStr.ModelOwnMaterial) : PropertiesPanel.MaterialName(chosen))));
            }

            ImGui.PopID();
        }

        PropertyGrid.Row(StageLoc.Get(StageStr.ModelCastShadow));
        bool cast = first.CastShadow;
        bool shadowMixed = false;
        foreach (var m in _meshes)
            shadowMixed |= m.CastShadow != cast;
        if (shadowMixed)
            MixedMarker();
        if (ImGui.Checkbox("##cast-shadow", ref cast))
            _stage.Undo.Execute(new SetFieldCommand<MeshComponent, bool>(_meshes, "shadow", static m => m.CastShadow, static (m, v) => m.CastShadow = v,
                cast, StageLoc.Get(StageStr.ModelCastShadow)));
        PropertyGrid.End();
        if (first.MaterialOverrides is { Count: > 0 } overrides && overrides.TryGetValue(0, out var shown))
            MaterialPreview(shown);
    }

    private void RefreshModel(Entity entity)
    {
        if (entity.Id == _modelFor && _modelSize.Length > 0)
            return;
        _modelFor = entity.Id;
        _modelSize = "—";
        _slotNames = Array.Empty<string>();
        var engine = _stage.Engine;
        if (engine is null || entity.Get<MeshComponent>() is not { } mesh)
            return;
        try
        {
            if (engine.Models.GetBounds(mesh.ModelAssetId) is { IsEmpty: false } bounds)
            {
                var size = (bounds.Max - bounds.Min) * Vector3.Abs(entity.Local.Scale);
                _modelSize = string.Create(CultureInfo.InvariantCulture, $"{size.X:0.###} × {size.Y:0.###} × {size.Z:0.###} m");
            }

            if (engine.Models.GetModelData(mesh.ModelAssetId) is { } model)
                _slotNames = model.Materials.Select((m, i) => string.IsNullOrWhiteSpace(m.Name) ? StageLoc.Format(StageStr.ModelSlot, i + 1) : m.Name).ToArray();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            _modelSize = e.Message;
        }
    }

    /// <summary>
    /// The built-in material dropdown: a swatch and the localised name per entry, grouped by the library's categories
    /// (plus "the model's own material" for model slots). Returns true when a material (or, with null, the own material)
    /// was chosen.
    /// </summary>
    private static bool MaterialCombo(string id, string? current, bool mixed, bool allowOwn, string? ownName, out string? chosen)
    {
        chosen = null;
        string preview = mixed ? StageLoc.Get(StageStr.PropsMixedShort)
            : current is null ? (ownName ?? StageLoc.Get(StageStr.ModelOwnMaterial))
            : PropertiesPanel.MaterialName(current);
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        bool open = ImGui.BeginCombo(id, Padded(preview));
        // The swatch of the current material inside the combo frame.
        if (!mixed)
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddCircleFilled(p + new Vector2(h * 0.55f, h * 0.5f), h * 0.3f, Theme.U32(current is null ? Theme.TextMuted : MaterialSwatch.FlatColor(current)), 16);
        }

        if (!open)
            return false;
        bool picked = false;
        if (allowOwn && ImGui.Selectable(ownName ?? StageLoc.Get(StageStr.ModelOwnMaterial), current is null))
        {
            picked = current is not null;
            chosen = null;
        }

        var library = MaterialLibrary.Default;
        foreach (var category in library.Categories)
        {
            ImGui.SeparatorText(MaterialLibrary.CategoryDisplayName(category));
            foreach (var m in library.InCategory(category))
            {
                MaterialSwatch.Flat(m.Id, ImGui.GetTextLineHeight());
                ImGui.SameLine();
                if (ImGui.Selectable(PropertiesPanel.MaterialName(m.Id), m.Id == current) && m.Id != current)
                {
                    chosen = m.Id;
                    picked = true;
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(m.Description);
            }
        }

        ImGui.EndCombo();
        return picked;
    }

    private static readonly Dictionary<string, string> s_padded = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string, string), string> s_materialNotes = new();

    /// <summary>The combo preview text indented past the swatch (cached per text).</summary>
    private static string Padded(string text)
    {
        if (!s_padded.TryGetValue(text, out var padded))
        {
            padded = "     " + text;
            if (s_padded.Count > 256)
                s_padded.Clear();
            s_padded[text] = padded;
        }

        return padded;
    }

    private static void MaterialPreview(string? materialId)
    {
        float size = MathF.Round(ImGui.GetTextLineHeight() * 5f);
        ImGui.Indent(MathF.Max(0f, (ImGui.GetContentRegionAvail().X - size) * 0.5f));
        MaterialSwatch.Sphere(materialId, size);
        ImGui.Unindent(MathF.Max(0f, (ImGui.GetContentRegionAvail().X - size) * 0.5f));
        if (MaterialLibrary.Default.Find(materialId) is { } m)
        {
            if (!s_materialNotes.TryGetValue((m.Id, Loc.Language), out var note))
            {
                note = string.Create(CultureInfo.InvariantCulture,
                    $"{m.Description} · ρ {m.LuminousReflectance:0.00} · {StageLoc.Get(StageStr.MaterialRoughness)} {m.Roughness:0.00}");
                s_materialNotes[(m.Id, Loc.Language)] = note;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(note);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
    }

    private static string? SlotMaterial(MeshComponent mesh, int slot) =>
        mesh.MaterialOverrides is { } overrides && overrides.TryGetValue(slot, out var id) ? id : null;

    private void RefreshFormats()
    {
        if (string.Equals(_formatsLanguage, Loc.Language, StringComparison.Ordinal))
            return;
        _formatsLanguage = Loc.Language;
        string unit = Loc.Get(Str.UnitMetres);
        _metres2 = "%.2f " + unit;
        _metres3 = "%.3f " + unit;
    }

    private string AreaText(float area)
    {
        if (area != _areaValue || _areaText.Length == 0)
        {
            _areaValue = area;
            _areaText = string.Create(CultureInfo.InvariantCulture, $"{area:0.##} m²");
        }

        return _areaText;
    }

    private static void MixedMarker()
    {
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(p - new Vector2(4f, 0f), p + new Vector2(-2f, ImGui.GetFrameHeight()), Theme.U32(Theme.Note));
    }

    private static void Muted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }
}
