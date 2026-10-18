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
    private string _nameBuffer = "";
    private Guid _nameFor;

    /// <summary>Creates the panel.</summary>
    public PropertiesPanel(WorkspaceContext context)
        : base("Properties", Icons.Sliders)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
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

        if (selection.Count > 1)
        {
            ImGui.TextColored(Theme.Accent, $"{Icons.Layers}  {Loc.Format(Str.PropSelectionCount, selection.Count)}");
            ImGui.Separator();
        }

        ImGui.PushFont(_ctx.Fonts.Heading, 0f);
        ImGui.TextUnformatted($"{IconFor(entity)}  {entity.Name}");
        ImGui.PopFont();
        ImGui.TextDisabled(entity.Id.ToString());
        ImGui.Spacing();

        if (ImGui.BeginTable("##props", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, 92 * Scale);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
            NameRow(entity);
            FlagsRow(entity);
            ImGui.EndTable();
        }

        if (ImGui.CollapsingHeader($"{Icons.Move3D}  {Loc.Get(Str.PropTransform)}", ImGuiTreeNodeFlags.DefaultOpen))
            TransformEditor(entity);

        if (ImGui.CollapsingHeader($"{Icons.Component}  {Loc.Get(Str.PropComponents)}", ImGuiTreeNodeFlags.DefaultOpen))
            Components(entity);
    }

    private float Scale => ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;

    private void NameRow(Entity entity)
    {
        if (_nameFor != entity.Id)
        {
            _nameFor = entity.Id;
            _nameBuffer = entity.Name;
        }

        Label(Str.PropName);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##name", ref _nameBuffer, 256, ImGuiInputTextFlags.EnterReturnsTrue) || ImGui.IsItemDeactivatedAfterEdit())
        {
            string name = _nameBuffer.Trim();
            if (name.Length > 0 && name != entity.Name)
                _ctx.Session.Undo.Execute(new SetValueCommand<string>($"Rename {entity.Name}", () => entity.Name, v => entity.Name = v, name));
            _nameBuffer = entity.Name;
        }
    }

    private void FlagsRow(Entity entity)
    {
        Label(Str.PropVisible);
        bool visible = entity.Visible;
        if (ImGui.Checkbox("##visible", ref visible))
            _ctx.Session.Undo.Execute(new SetValueCommand<bool>(visible ? $"Show {entity.Name}" : $"Hide {entity.Name}", () => entity.Visible,
                v => entity.Visible = v, visible));
        ImGui.SameLine(0, 24 * Scale);
        bool locked = entity.Locked;
        if (ImGui.Checkbox(Loc.Get(Str.PropLocked), ref locked))
            _ctx.Session.Undo.Execute(new SetValueCommand<bool>(locked ? $"Lock {entity.Name}" : $"Unlock {entity.Name}", () => entity.Locked,
                v => entity.Locked = v, locked));
        Label(Str.PropLayer);
        ImGui.TextUnformatted(_ctx.Session.Scene.FindLayer(entity.LayerId)?.Name ?? "—");
    }

    private void TransformEditor(Entity entity)
    {
        var local = entity.Local;
        bool imperial = _ctx.Session.Scene.Settings.DisplayUnits == UnitSystem.Imperial;
        float unit = imperial ? FeetPerMetre : 1f;
        string unitFormat = imperial ? "%.3f ft" : "%.3f m";
        if (!ImGui.BeginTable("##transform", 2, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, 92 * Scale);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

        Label(Str.PropPosition);
        var position = local.Position * unit;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat3("##position", (float*)&position, 0.01f, unitFormat))
            Apply(entity, local with { Position = position / unit }, "Move", "position");
        EndEdit();

        Label(Str.PropRotation);
        var euler = TransformMath.ToEulerDegrees(local.Rotation);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat3("##rotation", (float*)&euler, 0.5f, "%.1f°"))
            Apply(entity, local with { Rotation = TransformMath.FromEulerDegrees(euler) }, "Rotate", "rotation");
        EndEdit();

        Label(Str.PropScale);
        var scale = local.Scale;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat3("##scale", (float*)&scale, 0.01f, 0.001f, 1000f, "%.3f"))
            Apply(entity, local with { Scale = Vector3.Max(scale, new Vector3(0.001f)) }, "Scale", "scale");
        EndEdit();
        ImGui.EndTable();
    }

    private void Apply(Entity entity, Transform value, string description, string field)
    {
        var targets = new[] { entity };
        _ctx.Session.Undo.Execute(new SetTransformCommand(targets, new[] { value }, $"{description} ({field})"));
    }

    private void EndEdit()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            _ctx.Session.Undo.BreakMerge();
    }

    private void Components(Entity entity)
    {
        if (entity.Components.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get(Str.PropNoComponents));
            return;
        }

        foreach (var component in entity.Components)
        {
            string name = component.GetType().Name.Replace("Component", "", StringComparison.Ordinal);
            ImGui.PushID(component.GetHashCode());
            ImGui.BulletText(name);
            switch (component)
            {
                case WallComponent wall:
                    ImGui.Indent();
                    ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"{wall.Kind}: {wall.Width:0.##} × {wall.Height:0.##} m, {wall.Thickness:0.###} m"));
                    MaterialCombo(wall);
                    ImGui.Unindent();
                    break;
                case MeshComponent mesh:
                    ImGui.Indent();
                    ImGui.TextDisabled(mesh.MaterialOverrides.TryGetValue(0, out var m) ? $"{Loc.Get(Str.PropMaterial)}: {m}" : mesh.ModelAssetId.ToString());
                    ImGui.Unindent();
                    break;
                case FixtureComponent fixture:
                    ImGui.Indent();
                    ImGui.TextDisabled(fixture.GdtfSpec);
                    ImGui.Unindent();
                    break;
            }

            ImGui.PopID();
        }
    }

    private void MaterialCombo(WallComponent wall)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"{Loc.Get(Str.PropMaterial)}##material", MaterialName(wall.MaterialId)))
            return;
        foreach (var (id, label) in BuiltInMaterials)
        {
            if (ImGui.Selectable(label, id == wall.MaterialId) && id != wall.MaterialId)
                _ctx.Session.Undo.Execute(ModifyComponentCommand.Create(wall, w => w.MaterialId = id, "Change material"));
        }

        ImGui.EndCombo();
    }

    private static string MaterialName(string id) =>
        BuiltInMaterials.FirstOrDefault(m => m.Id == id).Label ?? id;

    /// <summary>The built-in material ids of Core with readable names (the material library lives in Candela.Assets).</summary>
    internal static readonly (string Id, string Label)[] BuiltInMaterials =
    {
        (MaterialIds.PaintedWallWhite, "Painted wall, white"),
        (MaterialIds.PaintedWallBlack, "Painted wall, black"),
        (MaterialIds.Concrete, "Concrete"),
        (MaterialIds.StageFloorBlack, "Stage floor, black"),
        (MaterialIds.DanceFloorWhite, "Dance floor, white"),
        (MaterialIds.WoodStage, "Wooden stage"),
        (MaterialIds.MoltonBlack, "Molton, black"),
        (MaterialIds.Cyclorama, "Cyclorama"),
        (MaterialIds.Carpet, "Carpet"),
        (MaterialIds.AluminiumTruss, "Aluminium (truss)"),
        (MaterialIds.SteelBlack, "Steel, black"),
        (MaterialIds.Mirror, "Mirror"),
        (MaterialIds.Glass, "Glass"),
        (MaterialIds.LedScreenOff, "LED screen (off)"),
        (MaterialIds.Default, "Default"),
    };

    private static string IconFor(Entity entity) =>
        entity.Has<FixtureComponent>() ? Icons.Lightbulb
        : entity.Has<WallComponent>() ? Icons.BrickWall
        : entity.Has<CableComponent>() ? Icons.Cable
        : entity.Has<ImageComponent>() ? Icons.Image
        : Icons.Box;

    private static void Label(Str key)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Loc.Get(key));
        ImGui.TableNextColumn();
    }

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
}
