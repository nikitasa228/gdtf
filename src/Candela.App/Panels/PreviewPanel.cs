using System.Numerics;
using Candela.App.Docking;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Engine;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The Preview panel (requirement "Превью"): the real-time quality of every viewport — resolution scale, shadows,
/// volumetric light, indirect light, bloom, tone mapping, auto exposure and EV trim (<see cref="PreviewSettings"/>, with
/// the preset dropdown Default / Custom / Save as…), the viewports' camera exposure (EV100, with an incident-light
/// reading of the stage) and grid, and the stage atmosphere (haze density and anisotropy, ambient light; undoable scene
/// settings).
/// </summary>
internal sealed class PreviewPanel : Panel
{
    private static readonly string[] ToneMappers = { "AgX", "ACES", "Neutral" };
    private static readonly int[] ShadowResolutions = { 512, 1024, 2048, 4096 };
    private static readonly string[] ShadowResolutionLabels = { "512", "1024", "2048", "4096" };
    private static readonly int[] SliceCounts = { 64, 96, 128, 192, 256 };
    private static readonly string[] SliceLabels = { "64", "96", "128", "192", "256" };
    private static readonly StageStr[] IndirectModes = { StageStr.PreviewIndirectOff, StageStr.PreviewIndirectRsm, StageStr.PreviewIndirectRayTraced };

    private readonly StagePanelContext _stage;
    private readonly Func<RendererInfo?> _renderer;
    private PresetCombo<PreviewSettings>? _presets;
    private float _ev100 = 8f;
    private bool _gridOn = true;

    /// <summary>Creates the panel for the editor.</summary>
    public PreviewPanel(WorkspaceContext context)
        : this(StagePanelContext.From(context ?? throw new ArgumentNullException(nameof(context))), () => context.Gpu.Renderer.Info)
    {
    }

    /// <summary>Creates the panel around a stage context (tests); <paramref name="renderer"/> tells what the GPU supports.</summary>
    internal PreviewPanel(StagePanelContext stage, Func<RendererInfo?> renderer)
        : base("Preview", Icons.Sparkles)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelPreview);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(360, 560);

    /// <inheritdoc/>
    public override void Draw()
    {
        var binding = _stage.Tools.Preview;
        var p = binding.Live;
        _presets ??= new PresetCombo<PreviewSettings>(binding, "preview");
        if (_presets.Draw())
            SyncViewportFields();
        ImGui.Spacing();

        bool edited = false;
        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Gauge, StageStr.PreviewQuality), ImGuiTreeNodeFlags.DefaultOpen) &&
            PropertyGrid.Begin("##preview-quality", 150f))
        {
            PropertyGrid.Row(Loc.Get(Str.PreviewResolutionScale));
            float resolution = p.ResolutionScale;
            if (ImGui.SliderFloat("##resolution", ref resolution, 0.5f, 1f, "%.2f×"))
            {
                p.ResolutionScale = Math.Clamp(resolution, 0.25f, 1f);
                edited = true;
            }

            PropertyGrid.Row(Loc.Get(Str.PreviewShadows));
            bool shadows = p.Shadows;
            if (ImGui.Checkbox("##shadows", ref shadows))
            {
                p.Shadows = shadows;
                edited = true;
            }

            ImGui.BeginDisabled(!p.Shadows);
            PropertyGrid.Row(StageLoc.Get(StageStr.PreviewShadowResolution));
            if (Choice("##shadow-res", ShadowResolutionLabels, ShadowResolutions, p.ShadowMapResolution, out int shadowResolution))
            {
                p.ShadowMapResolution = shadowResolution;
                edited = true;
            }

            PropertyGrid.Row(StageLoc.Get(StageStr.PreviewShadowedLights));
            int shadowed = p.MaxShadowedLights;
            if (ImGui.SliderInt("##shadowed", ref shadowed, 0, 256))
            {
                p.MaxShadowedLights = shadowed;
                edited = true;
            }

            ImGui.EndDisabled();

            PropertyGrid.Row(Loc.Get(Str.PreviewVolumetrics));
            bool volumetrics = p.Volumetrics;
            if (ImGui.Checkbox("##volumetrics", ref volumetrics))
            {
                p.Volumetrics = volumetrics;
                edited = true;
            }

            ImGui.BeginDisabled(!p.Volumetrics);
            PropertyGrid.Row(StageLoc.Get(StageStr.PreviewSlices));
            if (Choice("##slices", SliceLabels, SliceCounts, p.VolumetricSlices, out int slices))
            {
                p.VolumetricSlices = slices;
                edited = true;
            }

            PropertyGrid.Row(StageLoc.Get(StageStr.PreviewRaymarchedBeams));
            int beams = p.MaxRaymarchedBeams;
            if (ImGui.SliderInt("##beams", ref beams, 0, 64))
            {
                p.MaxRaymarchedBeams = beams;
                edited = true;
            }

            PropertyGrid.Row(StageLoc.Get(StageStr.PreviewTemporal));
            bool temporal = p.TemporalAccumulation;
            if (ImGui.Checkbox("##temporal", ref temporal))
            {
                p.TemporalAccumulation = temporal;
                edited = true;
            }

            ImGui.EndDisabled();

            PropertyGrid.Row(Loc.Get(Str.PreviewIndirect));
            edited |= IndirectCombo(p);
            PropertyGrid.End();
        }

        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Camera, StageStr.PreviewImage), ImGuiTreeNodeFlags.DefaultOpen) &&
            PropertyGrid.Begin("##preview-image", 150f))
        {
            PropertyGrid.Row(Loc.Get(Str.PreviewBloom));
            bool bloom = p.Bloom;
            if (ImGui.Checkbox("##bloom", ref bloom))
            {
                p.Bloom = bloom;
                edited = true;
            }

            ImGui.BeginDisabled(!p.Bloom);
            PropertyGrid.Row(StageLoc.Get(StageStr.PreviewBloomIntensity));
            float bloomIntensity = p.BloomIntensity;
            if (ImGui.SliderFloat("##bloom-intensity", ref bloomIntensity, 0f, 0.3f, "%.3f"))
            {
                p.BloomIntensity = Math.Clamp(bloomIntensity, 0f, 1f);
                edited = true;
            }

            ImGui.EndDisabled();
            PropertyGrid.Row(Loc.Get(Str.PreviewToneMapper));
            int tone = Math.Clamp((int)p.ToneMapper, 0, ToneMappers.Length - 1);
            if (ImGui.Combo("##tone", ref tone, ToneMappers, ToneMappers.Length))
            {
                p.ToneMapper = (ToneMapper)tone;
                edited = true;
            }

            PropertyGrid.Row(Loc.Get(Str.PreviewAutoExposure));
            bool auto = p.AutoExposure;
            if (ImGui.Checkbox("##auto-exposure", ref auto))
            {
                p.AutoExposure = auto;
                edited = true;
            }

            PropertyGrid.Row(Loc.Get(Str.PreviewExposure));
            float trim = p.ExposureCompensation;
            if (ImGui.SliderFloat("##ev", ref trim, -6f, 6f, "%+.1f EV"))
            {
                p.ExposureCompensation = Math.Clamp(trim, -10f, 10f);
                edited = true;
            }

            ImGui.BeginDisabled(p.AutoExposure);
            PropertyGrid.Row(StageLoc.Get(StageStr.PreviewCameraEv));
            CameraExposure();
            ImGui.EndDisabled();
            PropertyGrid.End();
        }

        if (edited)
            binding.MarkEdited();

        if (ImGui.CollapsingHeader(StageLoc.With(Icons.CloudFog, StageStr.PreviewStage), ImGuiTreeNodeFlags.DefaultOpen) &&
            PropertyGrid.Begin("##preview-stage", 150f))
        {
            StageSettings(_stage.Scene);
            PropertyGrid.End();
        }
    }

    private bool IndirectCombo(PreviewSettings p)
    {
        bool rayTracing = _renderer()?.SupportsRayTracing ?? true;
        int indirect = Math.Clamp((int)p.Indirect, 0, IndirectModes.Length - 1);
        bool changed = false;
        if (ImGui.BeginCombo("##indirect", StageLoc.Get(IndirectModes[indirect])))
        {
            for (int i = 0; i < IndirectModes.Length; i++)
            {
                bool unavailable = i == (int)IndirectLighting.RayTraced && !rayTracing;
                ImGui.BeginDisabled(unavailable);
                if (ImGui.Selectable(StageLoc.Get(IndirectModes[i]), i == indirect) && i != indirect)
                {
                    p.Indirect = (IndirectLighting)i;
                    changed = true;
                }

                ImGui.EndDisabled();
                if (unavailable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(StageLoc.Get(StageStr.PreviewNoRayTracing));
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void CameraExposure()
    {
        var viewport = _stage.Tools.ActiveViewport;
        if (viewport is not null && !ImGui.IsAnyItemActive())
            _ev100 = viewport.Camera.Ev100;
        var style = ImGui.GetStyle();
        float meter = ImGui.CalcTextSize(StageLoc.Get(StageStr.PreviewMeter)).X + 2f * style.FramePadding.X;
        ImGui.SetNextItemWidth(MathF.Max(60f, ImGui.GetContentRegionAvail().X - meter - style.ItemInnerSpacing.X));
        if (ImGui.SliderFloat("##ev100", ref _ev100, -2f, 18f, "EV %.1f"))
            _stage.Tools.ApplyEv100ToViewports(_ev100);
        ImGui.SameLine(0f, style.ItemInnerSpacing.X);
        var engine = _stage.Engine;
        ImGui.BeginDisabled(engine is null);
        if (ImGui.Button(StageLoc.Get(StageStr.PreviewMeter)) && engine is not null)
        {
            var bounds = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);
            _ev100 = engine.LightMeter.IncidentEv100(bounds, bounds.IsEmpty ? 0f : bounds.Min.Z + 0.01f, _ev100);
            _stage.Tools.ApplyEv100ToViewports(_ev100);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(StageLoc.Get(StageStr.PreviewMeterHint));
    }

    private void StageSettings(Scene scene)
    {
        var settings = scene.Settings;
        PropertyGrid.Row(StageLoc.Get(StageStr.PreviewHaze));
        float haze = settings.BaseHazeScattering;
        if (ImGui.SliderFloat("##haze", ref haze, 0f, 0.2f, "%.3f /m", ImGuiSliderFlags.Logarithmic))
            _stage.Undo.Execute(SceneSetting(scene, "haze", s => s.BaseHazeScattering, (s, v) => s.BaseHazeScattering = v, Math.Clamp(haze, 0f, 1f)));
        EndDrag();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(StageStr.PreviewHazeHint));
        PropertyGrid.Row(StageLoc.Get(StageStr.PreviewHazeAnisotropy));
        float g = settings.HazeAnisotropy;
        if (ImGui.SliderFloat("##anisotropy", ref g, 0f, 0.95f, "%.2f"))
            _stage.Undo.Execute(SceneSetting(scene, "anisotropy", s => s.HazeAnisotropy, (s, v) => s.HazeAnisotropy = v, Math.Clamp(g, 0f, 0.99f)));
        EndDrag();
        PropertyGrid.Row(StageLoc.Get(StageStr.PreviewAmbient));
        float ambient = settings.AmbientLuminance;
        if (ImGui.SliderFloat("##ambient", ref ambient, 0f, 5f, "%.3f cd/m²", ImGuiSliderFlags.Logarithmic))
            _stage.Undo.Execute(SceneSetting(scene, "ambient", s => s.AmbientLuminance, (s, v) => s.AmbientLuminance = v, Math.Clamp(ambient, 0f, 100f)));
        EndDrag();

        PropertyGrid.Row(StageLoc.Get(StageStr.PreviewGrid));
        if (_stage.Tools.ActiveViewport is { } viewport && !ImGui.IsAnyItemActive())
            _gridOn = viewport.View.ShowGrid;
        if (ImGui.Checkbox("##grid", ref _gridOn))
            _stage.Tools.ApplyGridToViewports(_gridOn);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(StageStr.PreviewGridHint));
        PropertyGrid.Row(StageLoc.Get(StageStr.PreviewGridSpacing));
        float spacing = settings.GridSpacing;
        if (ImGui.DragFloat("##grid-spacing", ref spacing, 0.01f, 0.05f, 10f, "%.2f m"))
            _stage.Undo.Execute(SceneSetting(scene, "grid", s => s.GridSpacing, (s, v) => s.GridSpacing = v, Math.Clamp(spacing, 0.05f, 100f)));
        EndDrag();
    }

    private void EndDrag()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            _stage.Undo.BreakMerge();
    }

    /// <summary>An undoable change of a scene setting; consecutive changes of the same setting merge (one slider drag).</summary>
    internal static SetValueCommand<float> SceneSetting(Scene scene, string key, Func<SceneSettings, float> get, Action<SceneSettings, float> set,
        float value) =>
        new($"Change {key}", () => get(scene.Settings), v => set(scene.Settings, v), value, mergeKey: (scene, key));

    private void SyncViewportFields()
    {
        if (_stage.Tools.ActiveViewport is { } viewport)
        {
            _ev100 = viewport.Camera.Ev100;
            _gridOn = viewport.View.ShowGrid;
        }
    }

    private static bool Choice(string id, string[] labels, int[] values, int current, out int selected)
    {
        int index = Array.IndexOf(values, current);
        string preview = index >= 0 ? labels[index] : current.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool changed = false;
        selected = current;
        if (ImGui.BeginCombo(id, preview))
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (ImGui.Selectable(labels[i], i == index) && i != index)
                {
                    selected = values[i];
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }
}
