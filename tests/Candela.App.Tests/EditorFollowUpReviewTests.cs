using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Core.Settings;
using Candela.Engine;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// Review probes of the editor follow-ups: a project opened and shown by the Preview and Render panels and the overlay
/// legends is not an unsaved change until something is edited.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class EditorFollowUpReviewTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public EditorFollowUpReviewTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
    }

    public void Dispose()
    {
        Loc.SetLanguage("en");
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Showing_an_opened_project_in_the_panels_is_not_an_unsaved_change()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        var engine = session.Engine!;
        using var tools = StageTestSupport.CreateTools();
        tools.External.CanSave = false;
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);

        // A project with values of its own: another work light, an edited preview look and lux gradient.
        tools.BindProject(engine);
        ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.On);
        tools.Preview.Live.Bloom = !tools.Preview.Live.Bloom;
        tools.Preview.MarkEdited();
        tools.Lux.Live.RangeMax *= 3f;
        tools.Lux.MarkEdited();
        tools.BindProject(engine);
        string project = _dir.File("fokina.candela");
        Assert.True(session.SaveAs(project).Succeeded);

        foreach (string path in new[] { EngineTestSupport.Fokina, project })
        {
            Assert.True(session.Open(path).Succeeded);
            var stage = StagePanelContext.ForEngine(engine, tools, new AppSettings());
            var preview = new PreviewPanel(stage, () => null);
            var render = new RenderPanel(stage, _ => { });
            var legend = new OverlayLegend { Expanded = true };
            using var ui = new HeadlessImGui(new Vector2(1400, 1200));
            foreach (string language in new[] { "en", "ru" })
            {
                Loc.SetLanguage(language);
                for (int frame = 0; frame < 4; frame++)
                {
                    session.Update(1 / 30.0);
                    tools.BindProject(engine);
                    ui.Frame(() =>
                    {
                        ImGui.SetNextWindowPos(Vector2.Zero);
                        ImGui.SetNextWindowSize(new Vector2(420, 1150));
                        ImGui.Begin("preview-host");
                        preview.Draw();
                        ImGui.End();
                        ImGui.SetNextWindowPos(new Vector2(430, 0));
                        ImGui.SetNextWindowSize(new Vector2(420, 1150));
                        ImGui.Begin("render-host");
                        render.Draw();
                        ImGui.End();
                        ImGui.SetNextWindowPos(new Vector2(860, 0));
                        ImGui.SetNextWindowSize(new Vector2(520, 700));
                        ImGui.Begin("viewport-host");
                        legend.DrawWindow(tools, engine, frame % 2 == 0 ? OverlayMode.Lux : OverlayMode.Noise, new Vector2(860, 0), new Vector2(520, 700));
                        ImGui.End();
                    });
                }
            }

            tools.BindProject(engine);
            Assert.False(engine.IsModified, $"'{Path.GetFileName(path)}' counts as changed right after it was opened and shown");
        }

        Loc.SetLanguage("en");
        Assert.Equal(WorkLightMode.On, engine.View.WorkLight);
    }
}
