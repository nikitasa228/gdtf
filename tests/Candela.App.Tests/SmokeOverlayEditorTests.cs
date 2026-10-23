using System.Numerics;
using Candela.App.Input;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Core.Presets;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Settings;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// The smoke overlay in the editor: the viewport selector, the View menu and its shortcut, the legend with the density
/// gradient, its presets and the project binding, and the emitter labels with their outputs in English and Russian.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class SmokeOverlayEditorTests
{
    private const string Hazer = "ADJ@Entour_Haze_PRO@2023-09-08_First_Release.gdtf";
    private const string Fogger = "PRstage@FOG_1500@Ver_1.2.gdtf";

    [Fact]
    public void The_viewport_menu_and_shortcuts_offer_the_smoke_overlay()
    {
        Assert.Contains(OverlayMode.Smoke, ViewportPanel.OverlayModes);
        Assert.Equal(Icons.CloudFog, ViewportPanel.OverlayIcon(OverlayMode.Smoke));
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("Smoke and haze (1/m)", ViewportPanel.OverlayLabel(OverlayMode.Smoke));
            Loc.SetLanguage("ru");
            Assert.Equal("Дым и дымка (1/м)", ViewportPanel.OverlayLabel(OverlayMode.Smoke));
        }
        finally
        {
            Loc.SetLanguage("en");
        }

        var set = KeyBindingSet.CreateDefault();
        AppActions.AddDefaults(set);
        Assert.Equal(new KeyChord("H", KeyModifiers.Alt), Assert.Single(set.ChordsFor(AppActions.OverlaySmoke)));
        Assert.Equal(AppActions.OverlaySmoke, Assert.Single(set.ActionsFor(new KeyChord("H", KeyModifiers.Alt))));
    }

    [Fact]
    public void The_smoke_gradient_is_the_projects_and_its_presets_round_trip()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        using var tools = StageTestSupport.CreateTools();
        tools.BindProject(engine);
        Assert.Same(tools.Smoke.Live, engine.View.View.SmokeGradient);
        Assert.Equal(PresetLibrary<ColorGradient>.DefaultName, tools.Smoke.CurrentName);

        tools.Smoke.Live.RangeMax = 5f;
        GradientEditor.AddStop(tools.Smoke.Live, 0.3f);
        tools.Smoke.MarkEdited();
        Assert.True(tools.Smoke.IsCustom);
        tools.BindProject(engine);
        Assert.True(engine.IsModified, "an edited smoke gradient is an unsaved change of the project");
        StageTestSupport.AddWall(engine, "Floor", 10, 8, WallKind.Floor);
        string path = dir.File("smoke.candela");
        Assert.True(engine.SaveAs(path).Success);

        engine.New("Other");
        tools.BindProject(engine);
        Assert.Equal(2f, tools.Smoke.Live.RangeMax);
        Assert.True(engine.Open(path).Success);
        tools.BindProject(engine);
        Assert.Equal(5f, tools.Smoke.Live.RangeMax);
        Assert.Contains(tools.Smoke.Live.Stops, s => MathF.Abs(s.Value - 0.3f) < 1e-6f);
        Assert.Equal(0f, tools.Smoke.Live.OutOfRangeColor.W); // clear air stays uncoloured through the sanitizing copy
        Assert.Same(tools.Smoke.Live, engine.View.View.SmokeGradient);
    }

    [Fact]
    public void The_legend_lists_the_air_and_the_emitters_and_the_labels_name_their_outputs()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        using var tools = StageTestSupport.CreateTools();
        StageTestSupport.AddWall(engine, "Floor", 12, 10, WallKind.Floor);
        var hazer = StageTestSupport.AddFixture(engine, "Hazer SL", 1, new Vector3(-2f, 0f, 0f), spec: Hazer);
        var fogger = StageTestSupport.AddFixture(engine, "Fogger SR", 2, new Vector3(2f, 0f, 0f), universe: 1, address: 20, spec: Fogger);
        hazer.Get<FixtureComponent>()!.GdtfMode = "2 Channel Mode ";
        hazer.Get<FixtureComponent>()!.ManualAttributes = new() { ["Haze1"] = 60f, ["Fan1"] = 100f };
        engine.Scene.Settings.BaseHazeScattering = 0.02f;
        for (int i = 0; i < 20; i++) engine.Tick(1.0 / 30);

        var labels = new SmokeEmitterLabels();
        using var ui = new HeadlessImGui(new Vector2(1200, 800));
        try
        {
            Loc.SetLanguage("en");
            ui.Frame(() => labels.Refresh(engine));
            var hazerEntry = Assert.Single(labels.Entries, e => e.DeviceId == hazer.Id);
            Assert.Equal("Hazer SL · haze 60 %, fan 100 %", hazerEntry.Label);
            Assert.Equal("Fogger SR · fog off", Assert.Single(labels.Entries, e => e.DeviceId == fogger.Id).Label);
            Assert.StartsWith("Haze 0.02 /m, densest ", labels.AirLine, StringComparison.Ordinal);

            Loc.SetLanguage("ru");
            ui.Frame(() => labels.Refresh(engine));
            Assert.Equal("Hazer SL · дымка 60 %, вентилятор 100 %", Assert.Single(labels.Entries, e => e.DeviceId == hazer.Id).Label);
            Assert.Equal("Fogger SR · дым выкл.", Assert.Single(labels.Entries, e => e.DeviceId == fogger.Id).Label);
            Assert.StartsWith("Дымка 0.02 /м", labels.AirLine, StringComparison.Ordinal);

            // the label follows the output; a label that did not change keeps its string (no allocation per frame)
            Loc.SetLanguage("en");
            ui.Frame(() => labels.Refresh(engine));
            string fogLabel = Assert.Single(labels.Entries, e => e.DeviceId == fogger.Id).Label;
            hazer.Get<FixtureComponent>()!.ManualAttributes["Haze1"] = 25f;
            hazer.Get<FixtureComponent>()!.NotifyChanged();
            for (int i = 0; i < 5; i++) engine.Tick(1.0 / 30);
            ui.Frame(() => labels.Refresh(engine));
            Assert.Equal("Hazer SL · haze 25 %, fan 100 %", Assert.Single(labels.Entries, e => e.DeviceId == hazer.Id).Label);
            Assert.Same(fogLabel, Assert.Single(labels.Entries, e => e.DeviceId == fogger.Id).Label);

            // the device removed: its label goes
            engine.Scene.Remove(fogger);
            engine.Tick(1.0 / 30);
            ui.Frame(() => labels.Refresh(engine));
            Assert.DoesNotContain(labels.Entries, e => e.DeviceId == fogger.Id);

            // the legend and the labels draw in both languages, in a viewport looking at the hazer
            var legend = new OverlayLegend { Expanded = true };
            var camera = new ViewportCamera();
            foreach (string language in new[] { "en", "ru" })
            {
                Loc.SetLanguage(language);
                for (int i = 0; i < 3; i++)
                {
                    ui.Frame(() =>
                    {
                        ImGui.SetNextWindowPos(Vector2.Zero);
                        ImGui.SetNextWindowSize(new Vector2(1000, 700));
                        ImGui.Begin("viewport-host");
                        labels.DrawLabels(engine, camera, Vector2.Zero, new Vector2(1000, 700));
                        legend.DrawWindow(tools, engine, OverlayMode.Smoke, Vector2.Zero, new Vector2(1000, 700));
                        ImGui.End();
                    });
                }
            }
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }
}
