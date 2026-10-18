using System.Numerics;
using Candela.App.Docking;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Gdtf;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The library panel's frame (requirement): sub-tabs "Fixtures" (with one category tab per fixture category — PROFILE …
/// GENERATOR FIRE) and "Models", and a search field. The fixture list itself, thumbnails with the hover animation and
/// drag-and-drop placement come with the stage engine's fixture library in the next build phase; until then each
/// category shows a clear note.
/// </summary>
internal sealed class LibraryPanel : Panel
{
    private readonly WorkspaceContext _ctx;
    private string _search = "";
    private int _category = -1;

    /// <summary>Creates the panel.</summary>
    public LibraryPanel(WorkspaceContext context)
        : base("Library", Icons.Library)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelLibrary);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(320, 520);

    /// <summary>Categories shown as sub-tabs (every category except OTHER).</summary>
    public static IReadOnlyList<FixtureCategory> Categories { get; } =
        FixtureCategories.All.Where(c => c != FixtureCategory.Other).ToArray();

    /// <inheritdoc/>
    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##library-tabs"))
            return;
        if (ImGui.BeginTabItem(Labels.With(Icons.Lightbulb, Str.LibFixtures)))
        {
            DrawFixtures();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(Labels.With(Icons.Box, Str.LibModels)))
        {
            PropertiesPanel.EmptyState(Icons.Package, Loc.Get(Str.LibWaitingForEngine));
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawFixtures()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", Labels.With(Icons.Search, Str.LibSearch), ref _search, 128);
        ImGui.Spacing();

        // Category chips that wrap to the panel width.
        float avail = ImGui.GetContentRegionAvail().X;
        float x = 0f;
        var style = ImGui.GetStyle();
        if (Chip(Loc.Get(Str.LibAll), _category < 0, ref x, avail, style))
            _category = -1;
        for (int i = 0; i < Categories.Count; i++)
        {
            if (Chip(CategoryNames[i], _category == i, ref x, avail, style))
                _category = i;
        }

        ImGui.NewLine();
        ImGui.Separator();
        string category = _category < 0 ? Loc.Get(Str.LibAll) : CategoryNames[_category];
        ImGui.TextDisabled(category);
        PropertiesPanel.EmptyState(CategoryIcon(_category < 0 ? FixtureCategory.Other : Categories[_category]), Loc.Get(Str.LibWaitingForEngine));
        _ = _ctx;
    }

    private static bool Chip(string label, bool selected, ref float x, float avail, ImGuiStylePtr style)
    {
        var size = ImGui.CalcTextSize(label) + style.FramePadding * 2;
        if (x > 0 && x + size.X > avail)
            x = 0;
        else if (x > 0)
            ImGui.SameLine();
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.55f));
        bool clicked = ImGui.SmallButton(label);
        if (selected)
            ImGui.PopStyleColor();
        x += size.X + style.ItemSpacing.X;
        return clicked;
    }

    private static readonly string[] CategoryNames = Categories.Select(c => c.DisplayName()).ToArray();

    /// <summary>Icon for a fixture category.</summary>
    public static string CategoryIcon(FixtureCategory c) => c switch
    {
        FixtureCategory.Profile => Icons.LampCeiling,
        FixtureCategory.Wash or FixtureCategory.Par => Icons.Sun,
        FixtureCategory.WashBeam or FixtureCategory.Beam or FixtureCategory.Bsw => Icons.Sparkle,
        FixtureCategory.Spot or FixtureCategory.FollowSpot => Icons.Crosshair,
        FixtureCategory.LedBar => Icons.Rows,
        FixtureCategory.Haze or FixtureCategory.Fog => Icons.CloudFog,
        FixtureCategory.Fan => Icons.Fan,
        FixtureCategory.Scanner or FixtureCategory.Laser => Icons.Zap,
        FixtureCategory.Strobe or FixtureCategory.Blinder => Icons.Siren,
        FixtureCategory.Uv => Icons.SunMedium,
        FixtureCategory.GeneratorFire => Icons.Flame,
        _ => Icons.Lightbulb,
    };
}
