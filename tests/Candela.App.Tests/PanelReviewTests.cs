using System.Net;
using System.Numerics;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Presets;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Settings;
using Candela.Dmx;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Gdtf;
using Candela.Protocols;
using Candela.Protocols.ArtNet;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// Regression tests of the review of the engine-backed panels: levels in percent (not candela), render-job errors
/// that must not escape into the frame loop, cancelled sequences, one undo step per discrete edit, the provenance of
/// manufacturer data, preset deletion, model sizes, the timecode source, the test target on a selected interface and
/// the slot view.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class PanelReviewTests
{
    /// <summary>A GDTF whose dimmer has the attribute definition's unit LuminousIntensity with physical values 0 … 1.</summary>
    private const string CandelaUnitDimmer = "GSARC@DOT_L_TW@Offical.gdtf";

    [Theory]
    [InlineData(AttributeKind.Dimmer, PhysicalUnit.LuminousIntensity, 0f, 1f, 100f, "%.0f%%")]
    [InlineData(AttributeKind.Dimmer, PhysicalUnit.None, 0f, 1f, 100f, "%.0f%%")]
    [InlineData(AttributeKind.Dimmer, PhysicalUnit.None, 0f, 100f, 1f, "%.0f%%")]
    [InlineData(AttributeKind.Ignored, PhysicalUnit.Percent, 0f, 1f, 100f, "%.0f%%")]
    [InlineData(AttributeKind.Ignored, PhysicalUnit.Percent, 0f, 100f, 1f, "%.0f%%")]
    [InlineData(AttributeKind.Ignored, PhysicalUnit.None, 0f, 0.05f, 100f, "%.1f%%")]
    [InlineData(AttributeKind.Pan, PhysicalUnit.Angle, -270f, 270f, 1f, null)]
    [InlineData(AttributeKind.Ignored, PhysicalUnit.LuminousIntensity, 0f, 1200f, 1f, null)]
    [InlineData(AttributeKind.Ignored, PhysicalUnit.None, 0f, 0f, 1f, null)]
    public void Levels_are_shown_in_percent_whatever_unit_the_gdtf_gives(AttributeKind kind, PhysicalUnit unit, float min, float max, float scale,
        string? format)
    {
        var (s, f) = FixtureControls.LevelDisplay(new AttributeClass(kind), unit, min, max);
        Assert.Equal(scale, s);
        Assert.Equal(format, f);
    }

    [Theory]
    [InlineData("%.1f°", 0.05f)]
    [InlineData("%.0f%%", 0.5f)]
    [InlineData("%.2f Hz", 0.005f)]
    [InlineData("—", 0.5f)]
    public void Numbers_that_round_to_zero_are_shown_as_zero_not_minus_zero(string format, float band)
    {
        Assert.Equal(band, FixtureControls.ZeroBandOf(format), 6);
        Assert.Equal(0f, FixtureControls.CleanZero(-band * 0.5f, band));
        Assert.False(float.IsNegative(FixtureControls.CleanZero(-band * 0.5f, band)));
        Assert.Equal(-band * 3f, FixtureControls.CleanZero(-band * 3f, band));
        // A motor that settles at −0.00004 rad reads "0.0°" in the current-state line.
        Assert.Equal("0.0", FixtureInspectorView.Degrees(-0.00004f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_dimmer_with_the_candela_unit_of_the_gdtf_definition_is_a_percent_control_not_cd()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var lamp = StageTestSupport.AddFixture(engine, "Dot", 1, new Vector3(0, 0, 4), spec: CandelaUnitDimmer);
        engine.Tick(0.02);
        var controls = new FixtureControls();
        controls.Refresh(engine, lamp);
        var dimmer = controls.Controls.First(c => c.Class.Kind == AttributeKind.Dimmer);
        Assert.Equal(PhysicalUnit.LuminousIntensity, dimmer.Unit);
        Assert.Equal(1f, dimmer.Max, 3);
        Assert.Equal(100f, dimmer.DisplayScale);
        Assert.DoesNotContain("cd", dimmer.Format, StringComparison.Ordinal);
        Assert.Contains("%%", dimmer.Format, StringComparison.Ordinal);
    }

    /// <summary>A renderer whose GPU work fails like a lost Vulkan device.</summary>
    private sealed class FailingRenderer : IRenderer
    {
        public RendererInfo Info => throw new InvalidOperationException("not used");

        public IRenderWorld World => throw new InvalidOperationException("not used");

        public int TargetsCreated { get; private set; }

        public IRenderTarget CreateTarget(int width, int height)
        {
            TargetsCreated++;
            throw new VulkanException(Silk.NET.Vulkan.Result.ErrorDeviceLost, "Creating the render target");
        }

        public void Render(IRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, float deltaTime) =>
            throw new VulkanException(Silk.NET.Vulkan.Result.ErrorDeviceLost, "Rendering");

        public Core.Geometry.ImageData ReadPixels(IRenderTarget target) => throw new InvalidOperationException("not used");

        public PickResult? Pick(IRenderTarget target, int x, int y) => null;

        public IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings) => throw new InvalidOperationException("not used");

        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_renderer_error_fails_the_render_job_instead_of_escaping_every_frame()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var job = new RenderJobRunner();
        var renderer = new FailingRenderer();
        var request = new RenderRequest(new RenderCamera(), new ViewOptions(), new PreviewSettings(), 64, 64, 1, 2, dir.File("a.png"), null);
        Assert.True(job.Start(request, null));
        // Before the review a VulkanException escaped from Step into the editor's frame loop, frame after frame.
        job.Step(null, renderer, 0);
        Assert.Equal(RenderJobState.Failed, job.State);
        Assert.True(job.MessageIsError);
        Assert.Contains("DeviceLost", job.Message, StringComparison.Ordinal);
        job.Step(null, renderer, 1);
        Assert.Equal(1, renderer.TargetsCreated);
        Assert.False(job.HoldsEngineClock);
    }

    [Fact]
    public void Cancelling_or_failing_a_timecode_sequence_stops_the_recording_it_plays()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddFixture(engine, "Spot", 1, new Vector3(0, 0, 6));
        engine.Tick(0.02);
        var frames = new List<DmxRecordedFrame>();
        for (int i = 0; i < 50; i++)
            frames.Add(new DmxRecordedFrame(i / 25.0, new Timecode(0, 0, i / 25, i % 25, TimecodeType.Ebu25), Array.Empty<DmxUniverseChange>()));
        var recording = new DmxRecording("tc", frames);
        var range = new RenderRange(recording, 0.5, 1.5, 25);

        using var job = new RenderJobRunner();
        var request = new RenderRequest(new RenderCamera(), new ViewOptions(), new PreviewSettings(), 32, 32, 1, 1, dir.File("seq.png"), range);
        Assert.True(job.Start(request, engine), job.Message);
        Assert.True(job.HoldsEngineClock);
        Assert.NotNull(engine.Player);
        job.Cancel();
        Assert.Equal(RenderJobState.Cancelled, job.State);
        Assert.False(job.HoldsEngineClock);
        Assert.Null(engine.Player);

        // A renderer failure in the middle of a sequence stops the playback too.
        Assert.True(job.Start(request, engine), job.Message);
        Assert.NotNull(engine.Player);
        job.Step(engine, new FailingRenderer(), 0);
        Assert.Equal(RenderJobState.Failed, job.State);
        Assert.Null(engine.Player);
    }

    [Fact]
    public void Material_and_shadow_picks_are_one_undo_step_each()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var wall = StageTestSupport.AddWall(engine, "Wall", 6, 3);
        var model = new Entity("Truss");
        var mesh = new MeshComponent { ModelAssetId = Guid.NewGuid() };
        model.Add(mesh);
        engine.Scene.Add(model);
        var stage = StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools());
        var view = new SurfaceInspectorView(stage);
        string original = wall.Get<WallComponent>()!.MaterialId;

        engine.Selection.Set(wall.Id);
        view.Collect(wall);
        view.SetWallMaterial(MaterialIds.Concrete);
        view.SetWallMaterial(MaterialIds.MoltonBlack);
        Assert.Equal(MaterialIds.MoltonBlack, wall.Get<WallComponent>()!.MaterialId);
        engine.Undo.Undo();
        Assert.Equal(MaterialIds.Concrete, wall.Get<WallComponent>()!.MaterialId);
        engine.Undo.Undo();
        Assert.Equal(original, wall.Get<WallComponent>()!.MaterialId);

        engine.Selection.Set(model.Id);
        view.Collect(model);
        bool cast = mesh.CastShadow;
        view.SetCastShadow(!cast);
        view.SetCastShadow(cast);
        view.SetSlotMaterial(0, MaterialIds.Concrete);
        view.SetSlotMaterial(0, null);
        Assert.Null(mesh.MaterialOverrides?.GetValueOrDefault(0));
        engine.Undo.Undo();
        Assert.Equal(MaterialIds.Concrete, mesh.MaterialOverrides![0]);
        engine.Undo.Undo();
        Assert.False(mesh.MaterialOverrides.ContainsKey(0));
        engine.Undo.Undo();
        Assert.Equal(!cast, mesh.CastShadow);
        engine.Undo.Undo();
        Assert.Equal(cast, mesh.CastShadow);
    }

    [Fact]
    public void Wheel_picks_mode_changes_and_fixture_ids_are_one_undo_step_each()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot", 7, Vector3.Zero);
        engine.Tick(0.02);
        var fixture = head.Get<FixtureComponent>()!;
        var stage = StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools());
        engine.Selection.Set(head.Id);
        var view = new FixtureInspectorView(stage);
        view.Controls.Refresh(engine, head);
        var gobo = view.Controls.Controls.First(c => c.Choices is { Length: > 3 } && c.Group == ControlGroup.GoboEffects);

        view.SetManual(engine, head, gobo, gobo.ChoiceValues![1], discrete: true);
        view.SetManual(engine, head, gobo, gobo.ChoiceValues[2], discrete: true);
        engine.Undo.Undo();
        Assert.Equal(gobo.ChoiceValues[1], fixture.ManualAttributes[gobo.Attribute]);
        engine.Undo.Undo();
        Assert.False(fixture.ManualAttributes.ContainsKey(gobo.Attribute));

        var modes = engine.Dmx.GetState(head.Id)!.Profile.FixtureType.DmxModes.Select(m => m.Name).ToList();
        Assert.True(modes.Count >= 2, "the Viper has several modes");
        string? before = fixture.GdtfMode;
        view.SetMode(head, fixture, modes[^1]);
        view.SetMode(head, fixture, modes[0] == modes[^1] ? modes[1] : modes[0]);
        engine.Undo.Undo();
        Assert.Equal(modes[^1], fixture.GdtfMode);
        engine.Undo.Undo();
        Assert.Equal(before, fixture.GdtfMode);

        view.SetFixtureId(head, fixture, 11);
        view.SetFixtureId(head, fixture, 12);
        engine.Undo.Undo();
        Assert.Equal(11, fixture.FixtureId);
        engine.Undo.Undo();
        Assert.Equal(7, fixture.FixtureId);
        view.SetFixtureId(head, fixture, -3);
        Assert.Equal(7, fixture.FixtureId);
    }

    [Fact]
    public void Estimated_manufacturer_values_are_marked_and_every_row_says_where_it_comes_from()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot", 7, Vector3.Zero);
        engine.Tick(0.02);
        var specs = engine.Dmx.GetState(head.Id)!.Profile.Specs!;
        Assert.Equal(SpecSource.Estimated, specs.SourceOf("noiseLevelDb"));
        Assert.Equal(SpecSource.Manual, specs.SourceOf("weightKg"));

        var facts = DeviceFacts.Build(engine, head);
        var noise = facts.Rows.Single(r => r.Label == StageStr.FactNoise);
        Assert.Equal(SpecSource.Estimated, noise.Source);
        Assert.StartsWith(DeviceFacts.EstimatedMark, noise.Value, StringComparison.Ordinal);
        var weight = facts.Rows.Single(r => r.Label == StageStr.FactWeight);
        Assert.Equal(SpecSource.Manual, weight.Source);
        Assert.DoesNotContain("≈", weight.Value, StringComparison.Ordinal);
        Assert.True(facts.HasEstimates);
        Assert.Contains(facts.References, r => r.Contains("martin.com", StringComparison.Ordinal));
        Assert.All(facts.Rows, r => Assert.Equal(r.Source == SpecSource.Estimated, r.Value.StartsWith(DeviceFacts.EstimatedMark, StringComparison.Ordinal)));
        Assert.Equal(StageStr.FactSourceEstimated, DeviceFacts.SourceText(SpecSource.Estimated));
        Assert.Null(DeviceFacts.SourceText(SpecSource.Unknown));

        // A power factor the GDTF declares is data (the Viper: 0.99) ...
        Assert.Equal(0.99f, engine.ElectricalProfiles.GetProfile(StageTestSupport.Viper, null)!.PowerFactor, 3);
        Assert.Contains("cos φ 0.99", facts.Rows.Single(r => r.Label == StageStr.FactPower).Value, StringComparison.Ordinal);
        // ... the 0.95 the editor assumes when nothing gives one is not.
        const string face = "ACME@ACME_(FACE_600)@ACME__(FACE_600).gdtf";
        var other = StageTestSupport.AddFixture(engine, "Face", 8, Vector3.One, universe: 2, spec: face);
        engine.Tick(0.02);
        Assert.Equal(Electrical.FixtureProfile.DefaultPowerFactor, engine.ElectricalProfiles.GetProfile(face, null)!.PowerFactor);
        Assert.DoesNotContain("cos", DeviceFacts.Build(engine, other).Rows.Single(r => r.Label == StageStr.FactPower).Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_the_selected_preset_keeps_the_values_in_use()
    {
        var store = new MemoryPresetStore();
        var live = new PreviewSettings();
        var binding = new PresetBinding<PreviewSettings>("preview", live, () => new PreviewSettings(), SettingsCopy.Copy, SettingsCopy.Sanitized, store);
        live.Shadows = false;
        live.VolumetricSlices = 64;
        binding.MarkEdited();
        Assert.True(binding.SaveAs("Fast"));
        Assert.Equal("Fast", binding.CurrentName);
        var combo = new PresetCombo<PreviewSettings>(binding, "test");
        Assert.True(combo.DeleteCurrent());
        // The viewports keep their look; the values are no saved preset any more.
        Assert.False(live.Shadows);
        Assert.Equal(64, live.VolumetricSlices);
        Assert.True(binding.IsCustom);
        Assert.DoesNotContain("Fast", binding.Names);
        Assert.False(combo.DeleteCurrent(), "Custom cannot be deleted");
        Assert.True(binding.Select(PresetLibrary<PreviewSettings>.DefaultName));
        Assert.False(combo.DeleteCurrent(), "Default cannot be deleted");
    }

    [Fact]
    public void The_model_size_follows_the_scale_and_its_unit_the_language()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        string folder = dir.File("models");
        Directory.CreateDirectory(folder);
        string obj = Path.Combine(folder, "box.obj");
        File.WriteAllText(obj, "v 0 0 0\nv 2 0 0\nv 2 1 0\nv 0 1 0\nv 0 0 3\nv 2 0 3\nv 2 1 3\nv 0 1 3\n" +
                               "f 1 2 3 4\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n");
        var result = engine.Import(obj);
        Assert.True(result.Success, result.Summary);
        var entity = engine.Scene.Find(result.Entities[0])!;
        var view = new SurfaceInspectorView(StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools()));
        Loc.SetLanguage("en");
        view.RefreshModel(entity);
        // OBJ is Y-up: its 1 m height becomes Z.
        Assert.Equal("2 × 3 × 1 m", view.ModelSize);
        var local = entity.Local;
        local.Scale = new Vector3(2f);
        entity.Local = local;
        view.RefreshModel(entity);
        Assert.Equal("4 × 6 × 2 m", view.ModelSize);
        Loc.SetLanguage("ru");
        try
        {
            view.RefreshModel(entity);
            Assert.Equal("4 × 6 × 2 м", view.ModelSize);
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void The_timecode_shows_its_sender_art_net_with_its_endpoint_or_the_recording_by_name()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        StageTestSupport.AddFixture(engine, "Spot 1", 1, new Vector3(0, 0, 6), universe: 1, address: 1);
        engine.Tick(0.02);
        using var model = new ExternalControl { CanSave = false };
        int port = StageTestSupport.FreeUdpPort();
        model.Settings.ArtNetPort = port;
        var app = new AppSettings();
        model.Bind(engine, app);
        model.SetEnabled(engine, app, ProtocolIds.ArtNet, true);
        model.Poll(engine);
        Assert.Null(model.LastTimecode);
        using (var sender = new ArtNetSender(IPAddress.Loopback))
        {
            var target = new IPEndPoint(IPAddress.Loopback, port);
            Assert.True(StageTestSupport.TickUntil(engine, () =>
            {
                sender.SendTimeCode(target, new Timecode(1, 0, 0, 5, TimecodeType.Ebu25));
                return model.LastTimecode is { Source.Protocol: ArtNetReceiver.SourceProtocol };
            }, TimeSpan.FromSeconds(10)), "a timecode over Art-Net is attributed to Art-Net");
        }

        // The hub's sample: the sender's address and port, the arrival time on the hub's clock.
        var art = model.LastTimecode!;
        Assert.Equal(new Timecode(1, 0, 0, 5, TimecodeType.Ebu25), art.Timecode);
        Assert.StartsWith("127.0.0.1:", art.Source.Endpoint, StringComparison.Ordinal);
        Assert.Equal("Art-Net from " + art.Source.Endpoint, ExternalControl.DescribeTimecodeSource(art.Source));
        Assert.InRange(model.SecondsSince(art), 0, 30);

        model.SetEnabled(engine, app, ProtocolIds.ArtNet, false);
        var frames = new List<DmxRecordedFrame>();
        for (int i = 0; i < 25; i++)
            frames.Add(new DmxRecordedFrame(i / 25.0, new Timecode(2, 0, 0, i, TimecodeType.Ebu25), Array.Empty<DmxUniverseChange>()));
        engine.Play(new DmxRecording("Show 100% %s", frames), 0);
        Assert.True(StageTestSupport.TickUntil(engine, () => model.LastTimecode?.Timecode.Hours == 2, TimeSpan.FromSeconds(5)));
        var played = model.LastTimecode!;
        Assert.Equal(DmxPlayer.SourceProtocol, played.Source.Protocol);
        Assert.Equal("recording 'Show 100% %s'", ExternalControl.DescribeTimecodeSource(played.Source));
        Loc.SetLanguage("ru");
        Assert.Equal("запись «Show 100% %s»", ExternalControl.DescribeTimecodeSource(played.Source));
        Assert.Equal("Art-Net от 10.0.0.9:6454", ExternalControl.DescribeTimecodeSource(new DmxSourceId(ArtNetReceiver.SourceProtocol, "10.0.0.9:6454")));
        Loc.SetLanguage("en");
        // A sender the registry does not know keeps its protocol id; an unnamed one says nothing.
        Assert.Equal("ltc from hw:1", ExternalControl.DescribeTimecodeSource(new DmxSourceId("ltc", "hw:1")));
        Assert.Equal("", ExternalControl.DescribeTimecodeSource(DmxInputHub.UnnamedSource));
        engine.Play(null);
    }

    [Fact]
    public void The_test_show_goes_to_the_selected_interface_instead_of_loopback()
    {
        using var model = new ExternalControl { CanSave = false };
        var nic = new Protocols.Networking.NetworkInterfaceInfo("eth9", "eth9", "Lighting network", IPAddress.Parse("10.0.0.20"),
            IPAddress.Parse("255.0.0.0"), IPAddress.Parse("10.255.255.255"), true, false, Array.Empty<byte>());
        var interfaces = new[] { nic };
        // All interfaces: this computer is reached over loopback.
        Assert.Equal(IPAddress.Loopback, model.EffectiveTestTarget(new AppSettings { NetworkInterface = "" }, interfaces));
        // A receiver bound to the lighting adapter (Windows) does not see loopback: the show goes to the adapter's address.
        Assert.Equal(nic.Address, model.EffectiveTestTarget(new AppSettings { NetworkInterface = "10.0.0.20" }, interfaces));
        Assert.Equal(nic.Address, model.EffectiveTestTarget(new AppSettings { NetworkInterface = "eth9" }, interfaces));
        // An explicit remote target is kept; an unknown interface falls back to the target as configured.
        Assert.Equal(IPAddress.Loopback, model.EffectiveTestTarget(new AppSettings { NetworkInterface = "eth7" }, interfaces));
        model.Settings.TestTarget = "10.0.0.99";
        Assert.Equal(IPAddress.Parse("10.0.0.99"), model.EffectiveTestTarget(new AppSettings { NetworkInterface = "eth9" }, interfaces));
        model.Settings.TestTarget = "nonsense";
        Assert.Null(model.EffectiveTestTarget(new AppSettings(), interfaces));
    }

    [Fact]
    public void The_slot_view_marks_patched_slots_and_names_the_channel()
    {
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var head = StageTestSupport.AddFixture(engine, "Spot 1", 101, Vector3.Zero, universe: 3, address: 11);
        engine.Tick(0.02);
        using var model = new ExternalControl { CanSave = false };
        model.Bind(engine, new AppSettings());
        model.Poll(engine);
        var profile = engine.Dmx.GetState(head.Id)!.Profile;
        var patched = model.PatchedSlots(3).ToArray();
        Assert.Equal(profile.Footprint, patched.Count(p => p));
        Assert.False(patched[9]);
        Assert.True(patched[10]);
        Assert.True(patched[10 + profile.Footprint - 1]);
        Assert.DoesNotContain(true, model.PatchedSlots(4).ToArray());

        int pan = profile.FindChannels("Pan")[0];
        var offsets = profile.Channels[pan].Offsets;
        Assert.True(offsets.Count >= 1);
        var users = ExternalControl.SlotUsers(model.Patch!, 3, 10 + offsets[0]).ToList();
        Assert.Single(users);
        Assert.Contains("Spot 1", users[0], StringComparison.Ordinal);
        Assert.Contains("Pan", users[0], StringComparison.Ordinal);
        if (offsets.Count > 1)
            Assert.Contains("fine", ExternalControl.SlotUsers(model.Patch!, 3, 10 + offsets[1]).Single(), StringComparison.Ordinal);
        Assert.Empty(ExternalControl.SlotUsers(model.Patch!, 3, 1));
    }

    [Fact]
    public void A_stand_in_gdtf_of_an_mvr_fixture_is_explained_above_its_manufacturer_data()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var result = engine.Open(StageTestSupport.Fokina);
        Assert.True(result.Success, result.Summary);
        engine.Tick(0.02);
        var blade = engine.Scene.Query<FixtureComponent>().First(t => t.Item1.Name == "MagicBlade R").Item1;
        // The importer rewrote the spec to the stand-in, so the resolution alone no longer tells it is one.
        Assert.Equal(FixtureMatchKind.ExactFileName, engine.Dmx.GetResolution(blade.Id)!.MatchKind);
        var facts = DeviceFacts.Build(engine, blade);
        Assert.Contains("MagicPanel", facts.Title, StringComparison.Ordinal);

        var view = new DeviceFactsView(StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools()));
        view.RefreshTypeIssues(engine, blade);
        Assert.Contains(view.TypeIssues, i => i.Code == "mvr.gdtf-substitute" && i.Message.Contains("MagicBlade R", StringComparison.Ordinal));
        Assert.True(view.TypeIssues.Count <= 3);
        // Walls and other non-fixtures have none.
        var wall = StageTestSupport.AddWall(engine, "Wall", 4, 3);
        view.RefreshTypeIssues(engine, wall);
        Assert.Empty(view.TypeIssues);
        using var ui = new HeadlessImGui(new Vector2(900, 1400));
        ui.Frame(() =>
        {
            ImGui.SetNextWindowSize(new Vector2(420, 1300));
            ImGui.Begin("facts-host");
            view.Draw(engine, blade, 92f);
            ImGui.End();
        });
    }

    [Fact]
    public void Unresolved_fixture_without_specs_still_shows_why_in_the_type_section()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var engine = StageTestSupport.CreateEngine(dir);
        var ghost = StageTestSupport.AddFixture(engine, "Ghost", 9, Vector3.Zero, spec: "Nobody@Nothing@v0.gdtf");
        engine.Tick(0.02);
        var facts = DeviceFacts.Build(engine, ghost);
        Assert.Empty(facts.Rows);
        Assert.NotNull(facts.Note);
        // Before the review the section (and with it the note) was hidden for a fixture without any rows.
        Assert.True(DeviceFactsView.ShowsTypeSection(facts, ghost));
        var console = StageTestSupport.AddConsole(engine, "Desk", "DMX A", 1);
        Assert.False(DeviceFactsView.ShowsTypeSection(DeviceFacts.Build(engine, console), console));
        var view = new DeviceFactsView(StagePanelContext.ForEngine(engine, StageTestSupport.CreateTools()));
        using var ui = new HeadlessImGui(new Vector2(900, 900));
        ui.Frame(() =>
        {
            ImGui.SetNextWindowSize(new Vector2(420, 800));
            ImGui.Begin("facts-host");
            view.Draw(engine, ghost, 92f);
            ImGui.End();
        });
    }
}
