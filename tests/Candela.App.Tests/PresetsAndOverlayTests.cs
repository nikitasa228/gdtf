using System.Numerics;
using System.Text.RegularExpressions;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Core.Presets;
using Candela.Core.Serialization;
using Candela.Engine;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// Preset dropdowns (Default / Custom / Save as…) of the Preview and Render settings and of the overlay gradients, the
/// binding of those settings to the project's <c>view</c> section, the gradient editor's value mapping and editing
/// rules, the overlay legends, material previews and the panel strings.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed partial class PresetsAndOverlayTests
{
    [Fact]
    public void Editing_switches_to_custom_and_named_presets_round_trip()
    {
        var store = new MemoryPresetStore();
        var live = new PreviewSettings();
        var binding = new PresetBinding<PreviewSettings>("preview", live, () => new PreviewSettings(), SettingsCopy.Copy, SettingsCopy.Sanitized, store);
        Assert.Equal(PresetLibrary<PreviewSettings>.DefaultName, binding.CurrentName);
        Assert.True(binding.IsAtDefaults);

        live.Shadows = false;
        live.VolumetricSlices = 64;
        binding.MarkEdited();
        Assert.True(binding.IsCustom);
        Assert.Contains(PresetLibrary<PreviewSettings>.CustomName, binding.Names);
        Assert.True(binding.SaveAs("Fast"));
        Assert.Contains("Fast", binding.Names);
        Assert.False(binding.SaveAs("Default"), "the built-in names are reserved");
        Assert.NotNull(binding.LastError);

        Assert.True(binding.Select(PresetLibrary<PreviewSettings>.DefaultName));
        Assert.True(live.Shadows);
        Assert.Equal(128, live.VolumetricSlices);
        long version = binding.Version;
        Assert.True(binding.Select("Fast"));
        Assert.False(live.Shadows);
        Assert.Equal(64, live.VolumetricSlices);
        Assert.True(binding.Version > version);

        // A hand-edited preset file with nonsense is made safe when selected.
        store.Save("preview", "Broken", CandelaJson.Serialize(new PreviewSettings
        {
            ResolutionScale = 99f,
            VolumetricSlices = -3,
            ExposureCompensation = 1e30f,
            Indirect = (IndirectLighting)77,
        }));
        binding.RefreshNames();
        Assert.True(binding.Select("Broken"));
        Assert.Equal(1f, live.ResolutionScale);
        Assert.Equal(16, live.VolumetricSlices);
        Assert.Equal(10f, live.ExposureCompensation);
        Assert.Equal(IndirectLighting.ReflectiveShadowMaps, live.Indirect);
        Assert.False(binding.Select("missing"));
        Assert.NotNull(binding.LastError);
        Assert.True(binding.Delete("Fast"));
        Assert.DoesNotContain("Fast", binding.Names);
    }

    [Fact]
    public void Settings_copies_cover_every_property()
    {
        var preview = new PreviewSettings
        {
            ResolutionScale = 0.5f,
            Shadows = false,
            ShadowMapResolution = 4096,
            MaxShadowedLights = 3,
            Volumetrics = false,
            VolumetricSlices = 64,
            MaxRaymarchedBeams = 2,
            TemporalAccumulation = false,
            Indirect = IndirectLighting.RayTraced,
            Bloom = false,
            BloomIntensity = 0.2f,
            ToneMapper = ToneMapper.Aces,
            AutoExposure = true,
            ExposureCompensation = -2f,
        };
        var copy = new PreviewSettings();
        SettingsCopy.Copy(preview, copy);
        Assert.Equal(CandelaJson.Serialize(preview), CandelaJson.Serialize(copy));
        Assert.Equal(14, typeof(PreviewSettings).GetProperties().Length);

        var final = new FinalRenderSettings
        {
            Width = 640,
            Height = 360,
            SamplesPerPixel = 8,
            MaxBounces = 2,
            MaxVolumeBounces = 1,
            Denoise = false,
            ToneMapper = ToneMapper.Neutral,
            Format = ImageFileFormat.Exr,
            OutputPath = "/x/y.png",
            FrameRate = 30,
        };
        var finalCopy = new FinalRenderSettings();
        SettingsCopy.Copy(final, finalCopy);
        Assert.Equal(CandelaJson.Serialize(final), CandelaJson.Serialize(finalCopy));
        Assert.Equal(10, typeof(FinalRenderSettings).GetProperties().Length);

        var gradient = ColorGradient.DefaultNoise();
        gradient.Logarithmic = true;
        var gradientCopy = new ColorGradient();
        SettingsCopy.Copy(gradient, gradientCopy);
        Assert.Equal(CandelaJson.Serialize(gradient), CandelaJson.Serialize(gradientCopy));
    }

    [Fact]
    public void Damaged_gradients_are_made_safe()
    {
        var nasty = new ColorGradient
        {
            RangeMin = float.NaN,
            RangeMax = -5f,
            Logarithmic = true,
            Stops = { new(float.PositiveInfinity, Vector4.One), new(-3f, Vector4.One), new(100f, new Vector4(2f, -1f, 0.5f, 1f)), new(10f, Vector4.Zero) },
        };
        var safe = SettingsCopy.Sanitized(nasty, ColorGradient.DefaultLux);
        Assert.Equal(2, safe.Stops.Count);
        Assert.True(safe.Stops[0].Value < safe.Stops[1].Value);
        Assert.True(safe.RangeMax > safe.RangeMin && safe.RangeMin > 0f);
        Assert.Equal(new Vector4(1f, 0f, 0.5f, 1f), safe.Stops[1].Color);
        Assert.Equal(ColorGradient.DefaultLux().Stops.Count, SettingsCopy.Sanitized(null, ColorGradient.DefaultLux).Stops.Count);
        Assert.Equal(ColorGradient.DefaultLux().Stops.Count, SettingsCopy.Sanitized(new ColorGradient { Stops = null! }, ColorGradient.DefaultLux).Stops.Count);
    }

    [Fact]
    public void Live_settings_are_the_projects_view_section_and_survive_save_and_open()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        using var tools = StageTestSupport.CreateTools();
        tools.BindProject(engine);
        Assert.Same(tools.Preview.Live, engine.View.Preview);
        Assert.Same(tools.FinalRender.Live, engine.View.FinalRender);
        Assert.Same(tools.Lux.Live, engine.View.View.LuxGradient);
        Assert.Same(tools.Noise.Live, engine.View.View.NoiseGradient);

        tools.Lux.Live.RangeMax = 20000f;
        GradientEditor.AddStop(tools.Lux.Live, 150f);
        tools.Lux.MarkEdited();
        tools.Preview.Live.Bloom = false;
        tools.Preview.MarkEdited();
        StageTestSupport.AddWall(engine, "Floor", 10, 8, Core.SceneGraph.Components.WallKind.Floor);
        string path = dir.File("show.candela");
        Assert.True(engine.SaveAs(path).Success);

        // A new project brings the defaults; opening the saved one brings its values back into the live objects.
        engine.New("Other");
        tools.BindProject(engine);
        Assert.Equal(5000f, tools.Lux.Live.RangeMax);
        Assert.Equal(PresetLibrary<ColorGradient>.DefaultName, tools.Lux.CurrentName);
        Assert.True(tools.Preview.Live.Bloom);
        Assert.True(engine.Open(path).Success);
        tools.BindProject(engine);
        Assert.Equal(20000f, tools.Lux.Live.RangeMax);
        Assert.Contains(tools.Lux.Live.Stops, s => s.Value == 150f);
        Assert.True(tools.Lux.IsCustom);
        Assert.False(tools.Preview.Live.Bloom);
        Assert.Same(tools.Lux.Live, engine.View.View.LuxGradient);
    }

    [Theory]
    [InlineData(false, 0f, 100f)]
    [InlineData(true, 1f, 5000f)]
    public void Gradient_bar_positions_and_values_are_inverse(bool log, float min, float max)
    {
        for (int i = 0; i <= 20; i++)
        {
            float t = i / 20f;
            float v = GradientEditor.ValueAt(t, min, max, log);
            Assert.Equal(t, GradientEditor.PositionOf(v, min, max, log), 3);
        }

        Assert.Equal(0f, GradientEditor.PositionOf(float.NaN, min, max, log));
        Assert.Equal(1f, GradientEditor.PositionOf(max * 10f, min, max, log));
    }

    [Fact]
    public void Gradient_editing_keeps_stops_sorted_ranges_valid_and_matches_the_renderers_colours()
    {
        var g = ColorGradient.DefaultLux();
        int index = GradientEditor.AddStop(g, 150f);
        Assert.Equal(150f, g.Stops[index].Value);
        Assert.Equal(g.Stops.OrderBy(s => s.Value).Select(s => s.Value), g.Stops.Select(s => s.Value));
        // The new stop takes the colour the gradient had there, so adding a stop changes nothing visible.
        var reference = ColorGradient.DefaultLux();
        Assert.True(Vector4.Distance(reference.Evaluate(150f), g.Stops[index].Color) < 1e-4f);

        // The editor's own interpolation equals the renderer's for any value and order of stops.
        var random = new Random(7);
        var shuffled = new ColorGradient { Logarithmic = true, RangeMin = 1, RangeMax = 5000, Stops = g.Stops.OrderBy(_ => random.Next()).ToList() };
        for (int i = 0; i < 200; i++)
        {
            float v = MathF.Pow(10f, (float)random.NextDouble() * 3.7f);
            Assert.True(Vector4.Distance(shuffled.Evaluate(v), GradientEditor.Evaluate(shuffled.Stops, v, true)) < 1e-4f, $"{v}");
        }

        // Moving a stop past its neighbour and releasing it re-sorts and keeps it selected.
        g.Stops[0] = g.Stops[0] with { Value = 4000f };
        int moved = GradientEditor.SortStops(g, 0);
        Assert.Equal(4000f, g.Stops[moved].Value);

        Assert.True(GradientEditor.SetRange(g, lower: true, 10f));
        Assert.Equal(10f, g.RangeMin);
        Assert.True(GradientEditor.SetRange(g, lower: true, 1e9f));
        Assert.True(g.RangeMin < g.RangeMax);
        Assert.True(GradientEditor.SetRange(g, lower: false, -50f));
        Assert.True(g.RangeMax > g.RangeMin);
        Assert.False(GradientEditor.SetRange(g, lower: false, float.NaN));
        var (lo, hi) = GradientEditor.Domain(g);
        Assert.True(lo > 0 && hi > lo);
        Assert.Equal((0f, 1f), GradientEditor.Domain(new ColorGradient { Stops = new(), RangeMin = float.NaN, RangeMax = float.NaN }));

        Assert.Equal("5k", GradientEditor.FormatValue(5000f));
        Assert.Equal("1.2k", GradientEditor.FormatValue(1200f));
        Assert.Equal("300", GradientEditor.FormatValue(300f));
        Assert.Equal("0.5", GradientEditor.FormatValue(0.5f));
        Assert.Equal(1300f, GradientEditor.Snap(1284f, true));
        Assert.Equal(45f, GradientEditor.Snap(45.3f, false));
    }

    [Fact]
    public void Gradient_editor_adds_drags_and_removes_stops_with_the_mouse()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 400));
        var editor = new GradientEditor("test", "lx");
        var g = ColorGradient.DefaultLux();
        int stops = g.Stops.Count;
        bool changed = false;
        Vector2 origin = default;
        void Draw()
        {
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(new Vector2(500, 200));
            ImGui.Begin("legend-host", ImGuiWindowFlags.NoMove);
            origin = ImGui.GetCursorScreenPos();
            changed |= editor.Draw(g, 400f);
            ImGui.End();
        }

        ui.Frame(Draw);
        var io = ImGui.GetIO();
        float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
        float barY = origin.Y + 7f * scale + 2f * scale + 8f * scale;
        // Click in the middle of the bar: a stop is added there.
        io.AddMousePosEvent(origin.X + 200f, barY);
        ui.Frame(Draw);
        io.AddMouseButtonEvent(0, true);
        ui.Frame(Draw);
        io.AddMouseButtonEvent(0, false);
        ui.Frame(Draw);
        Assert.True(changed);
        Assert.Equal(stops + 1, g.Stops.Count);
        Assert.True(editor.Selected >= 0);
        Assert.True(editor.RemoveSelected(g));
        Assert.Equal(stops, g.Stops.Count);
        Assert.False(editor.RemoveSelected(g));
    }

    [Fact]
    public void Legends_draw_for_every_overlay_and_edit_the_shared_gradient()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        using var tools = StageTestSupport.CreateTools();
        var legend = new OverlayLegend { Expanded = true };
        using var ui = new HeadlessImGui(new Vector2(1200, 800));
        bool hovered = false;
        foreach (string language in new[] { "en", "ru" })
        {
            Loc.SetLanguage(language);
            foreach (var mode in new[] { OverlayMode.Lux, OverlayMode.Noise, OverlayMode.Cables })
            {
                for (int i = 0; i < 3; i++)
                {
                    ui.Frame(() =>
                    {
                        ImGui.SetNextWindowPos(Vector2.Zero);
                        ImGui.SetNextWindowSize(new Vector2(1000, 700));
                        ImGui.Begin("viewport-host");
                        hovered |= legend.DrawWindow(tools, engine, mode, new Vector2(0, 0), new Vector2(1000, 700));
                        ImGui.End();
                    });
                }
            }
        }

        Loc.SetLanguage("en");
        // A legend in a view too small for it is not drawn.
        ui.Frame(() =>
        {
            ImGui.Begin("tiny-host");
            Assert.False(legend.DrawWindow(tools, engine, OverlayMode.Lux, Vector2.Zero, new Vector2(60, 40)));
            ImGui.End();
        });
        _ = hovered;
    }

    [Fact]
    public void Material_previews_exist_for_every_builtin_material_and_textures_show()
    {
        var library = Assets.MaterialLibrary.Default;
        foreach (var m in library.Materials)
        {
            var colors = MaterialSwatch.SphereColors(m.Id);
            Assert.Equal(MaterialSwatch.Cells * MaterialSwatch.Cells, colors.Length);
            int centre = MaterialSwatch.Cells / 2 * MaterialSwatch.Cells + MaterialSwatch.Cells / 2;
            Assert.NotEqual(0u, colors[centre]);
            Assert.Equal(0u, colors[0]);
        }

        // Wood shows its planks: the sphere has more distinct colours than the untextured white wall.
        int Distinct(string id) => MaterialSwatch.SphereColors(id).Where(c => c != 0).Distinct().Count();
        Assert.True(Distinct(Core.SceneGraph.Components.MaterialIds.WoodStage) > Distinct(Core.SceneGraph.Components.MaterialIds.PaintedWallWhite));
        Assert.Same(MaterialSwatch.SphereColors("unknown-id"), MaterialSwatch.SphereColors("unknown-id"));
    }

    [Fact]
    public void Wall_fields_of_a_multi_selection_change_together_in_one_step()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var a = StageTestSupport.AddWall(engine, "A", 4, 3);
        var b = StageTestSupport.AddWall(engine, "B", 6, 2);
        var walls = new[] { a.Get<Core.SceneGraph.Components.WallComponent>()!, b.Get<Core.SceneGraph.Components.WallComponent>()! };
        var view = new SurfaceInspectorView(StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools()));
        engine.Selection.Set(new[] { a.Id, b.Id });
        view.Collect(b);
        Assert.Equal(2, view.Walls.Count);
        Assert.Same(walls[1], view.Walls[0]);
        engine.Undo.Execute(new SetFieldCommand<Core.SceneGraph.Components.WallComponent, float>(view.Walls, "width", w => w.Width,
            (w, v) => w.Width = v, 5f, "width"));
        engine.Undo.Execute(new SetFieldCommand<Core.SceneGraph.Components.WallComponent, float>(view.Walls, "width", w => w.Width,
            (w, v) => w.Width = v, 5.5f, "width"));
        Assert.All(walls, w => Assert.Equal(5.5f, w.Width));
        engine.Undo.Undo();
        Assert.False(engine.Undo.CanUndo);
        Assert.Equal(4f, walls[0].Width);
        Assert.Equal(6f, walls[1].Width);
    }

    [Fact]
    public void Every_panel_string_has_english_and_russian_with_the_same_placeholders()
    {
        var keys = Enum.GetValues<StageStr>();
        var entries = StageLoc.Entries.ToDictionary(e => e.Key);
        Assert.Equal(keys.Length, entries.Count);
        foreach (var key in keys)
        {
            Assert.True(entries.TryGetValue(key, out var e), $"{key} has no text");
            Assert.False(string.IsNullOrWhiteSpace(e.En), $"{key} English");
            Assert.False(string.IsNullOrWhiteSpace(e.Ru), $"{key} Russian");
            Assert.Equal(Placeholders(e.En), Placeholders(e.Ru));
        }

        foreach (var protocol in Protocols.ProtocolRegistry.All)
        {
            Assert.True(StageLoc.ProtocolTranslations.TryGetValue(protocol.Id, out var ru), protocol.Id);
            Assert.False(string.IsNullOrWhiteSpace(ru.Description));
            Assert.False(string.IsNullOrWhiteSpace(ru.Reason));
        }

        Loc.SetLanguage("ru");
        Assert.Equal("Внешняя связь", StageLoc.Get(StageStr.PanelExternalControl));
        Assert.EndsWith("Внешняя связь", StageLoc.With("*", StageStr.PanelExternalControl), StringComparison.Ordinal);
        Loc.SetLanguage("en");
        Assert.Equal("External Control", StageLoc.Get(StageStr.PanelExternalControl));
    }

    [Theory]
    [InlineData("Control1", "Control 1")]
    [InlineData("LEDFrequency", "LED Frequency")]
    [InlineData("Color_Macro", "Color Macro")]
    [InlineData("NoFeature", "No Feature")]
    [InlineData("", "")]
    public void Unmodelled_attribute_names_get_spaces(string attribute, string label) =>
        Assert.Equal(label, AttributeLabels.SplitWords(attribute));

    private static string Placeholders(string text) =>
        string.Join(",", PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).Order());

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();
}
