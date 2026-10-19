using System.Globalization;
using System.Numerics;
using Candela.App.Docking;
using Candela.App.Editing;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Settings;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// Minimal properties panel (the full fixture view is part 2): name, visibility and lock, numeric transform (position
/// in the display units, rotation as X/Y/Z degrees, scale) and the component list of the primary selection, plus a
/// material dropdown for walls/floors. Every edit is an undo step; dragging a field merges into one step.
/// </summary>
internal sealed unsafe class PropertiesPanel : Panel
{
    private const float FeetPerMetre = 3.280839895f;
    private readonly WorkspaceContext _ctx;
    private readonly byte[] _nameUtf8 = new byte[256];
    private readonly List<string> _componentNames = new();
    private readonly List<string?> _componentDetails = new();
    private Guid _nameFor;
    private string? _nameShown;
    private Entity? _cachedEntity;
    private long _cachedVersion = -1;
    private int _cachedSelectionCount = -1;
    private string _cachedLanguage = "";
    private string _headerText = "";
    private string _idText = "";
    private string _idShort = "";
    private string _selectionText = "";
    private readonly AxisFormats _formats = new();
    private readonly EulerDisplay _euler = new();
    private readonly StagePropertiesSection _stage;

    /// <summary>Creates the panel.</summary>
    public PropertiesPanel(WorkspaceContext context)
        : base("Properties", Icons.Sliders)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _stage = new StagePropertiesSection(Session.StagePanelContext.From(context));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelProperties);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(340, 480);

    /// <inheritdoc/>
    public override void Draw()
    {
        var session = _ctx.Session;
        var selection = session.Selection;
        if (selection.Primary is not { } id || session.Scene.Find(id) is not { } entity)
        {
            EmptyState(Icons.Pointer, Loc.Get(Str.PropNothingSelected));
            return;
        }

        RefreshTexts(entity, selection.Count);
        if (selection.Count > 1)
        {
            ImGui.TextColored(Theme.Accent, _selectionText);
            ImGui.Separator();
        }

        // Long names wrap; the id is shortened when the panel is too narrow for it (the tooltip shows it whole).
        ImGui.PushFont(_ctx.Fonts.Heading, 0f);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(_headerText);
        ImGui.PopTextWrapPos();
        ImGui.PopFont();
        bool idFits = ImGui.CalcTextSize(_idText).X <= ImGui.GetContentRegionAvail().X;
        ImGui.TextDisabled(idFits ? _idText : _idShort);
        if (!idFits && ImGui.IsItemHovered())
            ImGui.SetTooltip(_idText);
        ImGui.Spacing();

        if (PropertyGrid.Begin("##props", LabelWidth))
        {
            NameRow(entity);
            FlagsRows(entity);
            PropertyGrid.End();
        }

        _stage.Draw(entity, LabelWidth);

        if (ImGui.CollapsingHeader(Labels.With(Icons.Move3D, Str.PropTransform), ImGuiTreeNodeFlags.DefaultOpen))
        {
            // A locked object (or one on a locked layer) does not move: the gizmo is hidden and the fields are read-only.
            bool readOnly = !SelectionController.IsEditable(_ctx.Session.Scene, entity);
            ImGui.BeginDisabled(readOnly);
            TransformEditor(entity);
            ImGui.EndDisabled();
        }

        if (ImGui.CollapsingHeader(Labels.With(Icons.Component, Str.PropComponents), ImGuiTreeNodeFlags.DefaultOpen))
            Components(entity);
    }

    /// <summary>Smallest label column width at 100 % (the grid widens it for longer, e.g. translated, labels).</summary>
    private const float LabelWidth = 92f;

    /// <summary>Rebuilds the displayed texts only when the entity, the scene, the selection size or the language changed.</summary>
    private void RefreshTexts(Entity entity, int selectionCount)
    {
        long version = _ctx.Session.Scene.Version;
        if (ReferenceEquals(entity, _cachedEntity) && version == _cachedVersion && selectionCount == _cachedSelectionCount &&
            string.Equals(_cachedLanguage, Loc.Language, StringComparison.Ordinal))
            return;
        _cachedEntity = entity;
        _cachedVersion = version;
        _cachedSelectionCount = selectionCount;
        _cachedLanguage = Loc.Language;
        _selectionText = $"{Icons.Layers}  {Loc.Format(Str.PropSelectionCount, selectionCount)}";
        _headerText = $"{IconFor(entity)}  {entity.Name}";
        _idText = entity.Id.ToString();
        _idShort = _idText[..8] + "…";
        _componentNames.Clear();
        _componentDetails.Clear();
        foreach (var component in entity.Components)
        {
            _componentNames.Add(component.GetType().Name.Replace("Component", "", StringComparison.Ordinal));
            _componentDetails.Add(component switch
            {
                WallComponent wall => string.Create(CultureInfo.InvariantCulture,
                    $"{wall.Kind}: {wall.Width:0.##} × {wall.Height:0.##} m, {wall.Thickness:0.###} m"),
                MeshComponent mesh => mesh.MaterialOverrides.TryGetValue(0, out var m) ? $"{Loc.Get(Str.PropMaterial)}: {m}" : mesh.ModelAssetId.ToString(),
                FixtureComponent fixture => fixture.GdtfSpec,
                _ => null,
            });
        }
    }

    private void NameRow(Entity entity)
    {
        PropertyGrid.Row(Str.PropName);
        // A UTF-8 buffer kept between frames: the field is edited in place without building strings every frame.
        // The field still being edited for an object that is no longer the primary selection (the selection changed
        // under the keyboard): drop that edit, or the half-typed name would be committed to the new object.
        uint fieldId = ImGui.GetID("##name");
        if (_nameFor != entity.Id && ImGuiP.GetActiveID() == fieldId)
            ImGuiP.ClearActiveID();
        bool editing = _nameFor == entity.Id && ImGuiP.GetActiveID() == fieldId;
        if (!editing && (_nameFor != entity.Id || !ReferenceEquals(_nameShown, entity.Name)))
            SetNameBuffer(entity);
        bool commit;
        fixed (byte* buffer = _nameUtf8)
        {
            commit = ImGui.InputText("##name", buffer, (nuint)_nameUtf8.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        }

        if (commit || ImGui.IsItemDeactivatedAfterEdit())
        {
            int length = Array.IndexOf(_nameUtf8, (byte)0);
            string name = System.Text.Encoding.UTF8.GetString(_nameUtf8, 0, length < 0 ? _nameUtf8.Length : length).Trim();
            if (name.Length > 0 && name != entity.Name)
                _ctx.Session.Undo.Execute(RenameCommand(entity, name));
            SetNameBuffer(entity);
        }
    }

    // The commands are built in their own methods: a lambda capturing a parameter makes C# allocate its closure at the
    // start of the enclosing method, which would happen every frame in the drawing code.
    private static SetValueCommand<string> RenameCommand(Entity entity, string name) =>
        new($"Rename {entity.Name}", () => entity.Name, v => entity.Name = v, name);

    private static SetValueCommand<bool> VisibleCommand(Entity entity, bool visible) =>
        new(visible ? $"Show {entity.Name}" : $"Hide {entity.Name}", () => entity.Visible, v => entity.Visible = v, visible);

    private static SetValueCommand<bool> LockedCommand(Entity entity, bool locked) =>
        new(locked ? $"Lock {entity.Name}" : $"Unlock {entity.Name}", () => entity.Locked, v => entity.Locked = v, locked);

    private void SetNameBuffer(Entity entity)
    {
        _nameFor = entity.Id;
        _nameShown = entity.Name;
        Array.Clear(_nameUtf8);
        // Truncate on a character boundary so the buffer always holds valid UTF-8 and a terminating zero.
        var encoder = System.Text.Encoding.UTF8.GetEncoder();
        encoder.Convert(entity.Name.AsSpan(), _nameUtf8.AsSpan(0, _nameUtf8.Length - 1), flush: true, out _, out _, out _);
    }

    // Visible and Locked get a row each: side by side the translated "Locked" label was cut in narrow panels.
    private void FlagsRows(Entity entity)
    {
        PropertyGrid.Row(Str.PropVisible);
        bool visible = entity.Visible;
        if (ImGui.Checkbox("##visible", ref visible))
            _ctx.Session.Undo.Execute(VisibleCommand(entity, visible));
        PropertyGrid.Row(Str.PropLocked);
        bool locked = entity.Locked;
        if (ImGui.Checkbox("##locked", ref locked))
            _ctx.Session.Undo.Execute(LockedCommand(entity, locked));
        PropertyGrid.Row(Str.PropLayer);
        ImGui.TextUnformatted(SceneLookup.LayerOf(_ctx.Session.Scene, entity.LayerId)?.Name ?? "—");
    }

    private void TransformEditor(Entity entity)
    {
        var local = entity.Local;
        bool imperial = _ctx.Session.Scene.Settings.DisplayUnits == UnitSystem.Imperial;
        float unit = imperial ? FeetPerMetre : 1f;
        _formats.Refresh(imperial);
        if (!PropertyGrid.Begin("##transform", LabelWidth))
            return;

        PropertyGrid.Row(Str.PropPosition);
        var position = local.Position * unit;
        if (AxisFields("##position", ref position, 0.01f, 0f, 0f, _formats.Position, _formats.WidestSample, out bool positionDone))
            Apply(entity, local with { Position = position / unit }, "Move", "position");
        EndEdit(positionDone);

        PropertyGrid.Row(Str.PropRotation);
        var euler = _euler.Get(entity, local.Rotation);
        if (AxisFields("##rotation", ref euler, 0.5f, 0f, 0f, _formats.Rotation, _formats.WidestSample, out bool rotationDone))
        {
            // The entered triple stays on screen (a Y beyond ±90° would otherwise flip to an equivalent triple).
            var rotation = TransformMath.FromEulerDegrees(euler);
            Apply(entity, local with { Rotation = rotation }, "Rotate", "rotation");
            _euler.Set(entity, euler, entity.Local.Rotation);
        }

        EndEdit(rotationDone);

        PropertyGrid.Row(Str.PropScale);
        var scale = local.Scale;
        if (AxisFields("##scale", ref scale, 0.01f, 0.001f, 1000f, _formats.Scale, _formats.WidestSample, out bool scaleDone))
            Apply(entity, local with { Scale = Vector3.Max(scale, new Vector3(0.001f)) }, "Scale", "scale");
        EndEdit(scaleDone);
        PropertyGrid.End();
    }

    /// <summary>
    /// Three drag fields for X, Y and Z, each labelled with its axis letter and marked with the axis colour on its left
    /// edge (X red, Y green, Z blue, as in the viewport). Side by side when a third of the column fits
    /// <paramref name="widestSample"/> (a typical value with its axis letter and unit); in a narrow panel or at a large UI
    /// scale they stack, one full-width field per axis, instead of cutting the numbers. <paramref name="finished"/> is
    /// true when one of them ended an edit this frame (the undo merge of the drag ends there).
    /// </summary>
    private static bool AxisFields(string id, ref Vector3 value, float speed, float min, float max, string[] formats, string widestSample,
        out bool finished)
    {
        var style = ImGui.GetStyle();
        float spacing = style.ItemInnerSpacing.X;
        float available = ImGui.GetContentRegionAvail().X;
        float width = MathF.Max(1f, (available - 2f * spacing) / 3f);
        bool stacked = AxisFieldsStack(width, ImGui.CalcTextSize(widestSample).X, style.FramePadding.X);
        if (stacked)
            width = MathF.Max(1f, available);
        bool changed = false;
        finished = false;
        ImGui.PushID(id);
        for (int axis = 0; axis < 3; axis++)
        {
            if (axis > 0 && !stacked)
                ImGui.SameLine(0, spacing);
            ImGui.PushID(axis);
            ImGui.SetNextItemWidth(width);
            float v = axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
            if (ImGui.DragFloat("##v", ref v, speed, min, max, formats[axis]))
            {
                changed = true;
                if (axis == 0)
                    value.X = v;
                else if (axis == 1)
                    value.Y = v;
                else
                    value.Z = v;
            }

            finished |= ImGui.IsItemDeactivatedAfterEdit();
            var rectMin = ImGui.GetItemRectMin();
            var rectMax = ImGui.GetItemRectMax();
            float stripe = MathF.Max(2f, MathF.Round(2f * style.FontScaleMain * style.FontScaleDpi));
            ImGui.GetWindowDrawList().AddRectFilled(rectMin, new Vector2(rectMin.X + stripe, rectMax.Y), Theme.U32(AxisColor(axis)),
                style.FrameRounding, ImDrawFlags.RoundCornersLeft);
            ImGui.PopID();
        }

        ImGui.PopID();
        return changed;
    }

    /// <summary>
    /// True when side-by-side axis fields of <paramref name="fieldWidth"/> pixels would cut a value as wide as
    /// <paramref name="sampleWidth"/>: the centred text needs its width plus a small margin on both sides (half the
    /// frame padding, at least the axis stripe) to stay readable.
    /// </summary>
    internal static bool AxisFieldsStack(float fieldWidth, float sampleWidth, float framePaddingX) =>
        fieldWidth < sampleWidth + MathF.Max(framePaddingX, 4f);

    private static Vector4 AxisColor(int axis) => axis == 0 ? Theme.AxisX : axis == 1 ? Theme.AxisY : Theme.AxisZ;

    private void Apply(Entity entity, Transform value, string description, string field)
    {
        var targets = new[] { entity };
        _ctx.Session.Undo.Execute(new SetTransformCommand(targets, new[] { value }, $"{description} ({field})"));
    }

    private void EndEdit(bool finished)
    {
        if (finished)
            _ctx.Session.Undo.BreakMerge();
    }

    private void Components(Entity entity)
    {
        if (entity.Components.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get(Str.PropNoComponents));
            return;
        }

        var components = entity.Components;
        for (int i = 0; i < components.Count && i < _componentNames.Count; i++)
        {
            var component = components[i];
            ImGui.PushID(i);
            ImGui.BulletText(_componentNames[i]);
            if (_componentDetails[i] is { } text)
            {
                ImGui.Indent();
                ImGui.TextDisabled(text);
                ImGui.Unindent();
            }

            ImGui.PopID();
        }
    }

    /// <summary>The localised name of a built-in material, or the id itself for other materials.</summary>
    internal static string MaterialName(string id)
    {
        foreach (var (materialId, label) in BuiltInMaterials)
            if (materialId == id)
                return Loc.Get(label);
        return id;
    }

    /// <summary>The built-in material ids of Core with their names (the material library lives in Candela.Assets).</summary>
    internal static readonly (string Id, Str Label)[] BuiltInMaterials =
    {
        (MaterialIds.PaintedWallWhite, Str.MaterialPaintedWallWhite),
        (MaterialIds.PaintedWallBlack, Str.MaterialPaintedWallBlack),
        (MaterialIds.Concrete, Str.MaterialConcrete),
        (MaterialIds.StageFloorBlack, Str.MaterialStageFloorBlack),
        (MaterialIds.DanceFloorWhite, Str.MaterialDanceFloorWhite),
        (MaterialIds.WoodStage, Str.MaterialWoodStage),
        (MaterialIds.MoltonBlack, Str.MaterialMoltonBlack),
        (MaterialIds.Cyclorama, Str.MaterialCyclorama),
        (MaterialIds.Carpet, Str.MaterialCarpet),
        (MaterialIds.AluminiumTruss, Str.MaterialAluminiumTruss),
        (MaterialIds.SteelBlack, Str.MaterialSteelBlack),
        (MaterialIds.Mirror, Str.MaterialMirror),
        (MaterialIds.Glass, Str.MaterialGlass),
        (MaterialIds.LedScreenOff, Str.MaterialLedScreenOff),
        (MaterialIds.Default, Str.MaterialDefault),
    };

    private static string IconFor(Entity entity) =>
        entity.Has<FixtureComponent>() ? Icons.Lightbulb
        : entity.Has<WallComponent>() ? Icons.BrickWall
        : entity.Has<CableComponent>() ? Icons.Cable
        : entity.Has<ImageComponent>() ? Icons.Image
        : Icons.Box;

    /// <summary>Centred icon + hint for empty panels.</summary>
    internal static void EmptyState(string icon, string text)
    {
        var avail = ImGui.GetContentRegionAvail();
        float wrap = MathF.Min(avail.X - 16, 320 * ImGui.GetStyle().FontScaleMain);
        var iconSize = ImGui.CalcTextSize(icon);
        var textSize = ImGui.CalcTextSize(text, false, wrap);
        float total = iconSize.Y * 2 + textSize.Y;
        var start = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(start.X + (avail.X - iconSize.X * 2) * 0.5f, start.Y + MathF.Max(0, (avail.Y - total) * 0.4f)));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.WithAlpha(Theme.TextMuted, 0.6f));
        ImGui.PushFont(ImGui.GetFont(), ImGui.GetStyle().FontSizeBase * 2f);
        ImGui.TextUnformatted(icon);
        ImGui.PopFont();
        ImGui.PopStyleColor();
        ImGui.SetCursorPosX(start.X + MathF.Max(0, (avail.X - MathF.Min(textSize.X, wrap)) * 0.5f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>printf formats of the X/Y/Z fields ("X %.3f m"), rebuilt only when the language or the units change.</summary>
    private sealed class AxisFormats
    {
        private static readonly string[] Axes = { "X", "Y", "Z" };
        private string _language = "";
        private bool? _imperial;

        public string[] Position { get; } = new string[3];

        public string[] Rotation { get; } = new string[3];

        public string[] Scale { get; } = new string[3];

        /// <summary>A typical widest field text ("X  -0.000 m"), to decide whether the fields fit side by side.</summary>
        public string WidestSample { get; private set; } = "X  -0.000 m";

        public void Refresh(bool imperial)
        {
            if (_imperial == imperial && string.Equals(_language, Loc.Language, StringComparison.Ordinal))
                return;
            _imperial = imperial;
            _language = Loc.Language;
            string unit = Loc.Get(imperial ? Str.UnitFeet : Str.UnitMetres);
            WidestSample = $"X  -0.000 {unit}";
            for (int i = 0; i < 3; i++)
            {
                Position[i] = $"{Axes[i]}  %.3f {unit}";
                Rotation[i] = $"{Axes[i]}  %.1f°";
                Scale[i] = $"{Axes[i]}  %.3f";
            }
        }
    }
}
