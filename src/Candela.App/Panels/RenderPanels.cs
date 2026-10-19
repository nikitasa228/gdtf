using System.Numerics;
using Candela.App.Docking;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The Render panel (requirement): final-render settings — output file, resolution, samples, bounces, format, a single
/// frame or a timecode range — and the "Render" button (also File → Render…). The path tracer arrives in a later
/// milestone: the button says so instead of failing.
/// </summary>
internal sealed unsafe class RenderPanel : Panel
{
    private static readonly string[] Formats = { "PNG", "EXR" };
    private readonly WorkspaceContext _ctx;
    private readonly Action<SessionResult> _report;
    private bool _range;
    private float _rangeStart;
    private float _rangeEnd = 10f;
    private string _output;

    /// <summary>Creates the panel; <paramref name="report"/> shows the result of the Render button.</summary>
    public RenderPanel(WorkspaceContext context, Action<SessionResult> report)
        : base("Render", Icons.Clapperboard)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _output = _ctx.Tools.FinalRender.OutputPath;
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelRender);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(360, 420);

    /// <inheritdoc/>
    public override void Draw()
    {
        var s = _ctx.Tools.FinalRender;
        if (PropertyGrid.Begin("##render", 130f))
        {
            PropertyGrid.Row(Str.RenderOutput);
            if (ImGui.InputText("##output", ref _output, 512))
                s.OutputPath = _output;
            PropertyGrid.Row(Str.RenderResolution);
            int* size = stackalloc int[] { s.Width, s.Height };
            if (ImGui.InputInt2("##resolution", size))
            {
                s.Width = Math.Clamp(size[0], 16, 16384);
                s.Height = Math.Clamp(size[1], 16, 16384);
            }

            PropertyGrid.Row(Str.RenderSamples);
            int spp = s.SamplesPerPixel;
            if (ImGui.SliderInt("##spp", ref spp, 1, 4096, "%d", ImGuiSliderFlags.Logarithmic))
                s.SamplesPerPixel = spp;
            PropertyGrid.Row(Str.RenderBounces);
            int bounces = s.MaxBounces;
            if (ImGui.SliderInt("##bounces", ref bounces, 1, 16))
                s.MaxBounces = bounces;
            PropertyGrid.Row(Str.RenderFormat);
            int format = (int)s.Format;
            if (ImGui.Combo("##format", ref format, Formats, Formats.Length))
                s.Format = (ImageFileFormat)format;
            PropertyGrid.End();
        }

        ImGui.Spacing();
        if (ImGui.RadioButton(Loc.Get(Str.RenderFrame), !_range))
            _range = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.Get(Str.RenderRange), _range))
            _range = true;
        if (_range && PropertyGrid.Begin("##range", 130f))
        {
            PropertyGrid.Row(Str.RenderRangeStart);
            ImGui.DragFloat("##start", ref _rangeStart, 0.1f, 0f, 36000f, "%.2f s");
            PropertyGrid.Row(Str.RenderRangeEnd);
            ImGui.DragFloat("##end", ref _rangeEnd, 0.1f, 0f, 36000f, "%.2f s");
            PropertyGrid.Row(Str.RenderFrameRate);
            float fps = (float)s.FrameRate;
            if (ImGui.DragFloat("##fps", ref fps, 0.1f, 1f, 120f, "%.2f fps"))
                s.FrameRate = fps;
            _rangeEnd = MathF.Max(_rangeEnd, _rangeStart);
            PropertyGrid.End();
        }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent);
        if (ImGui.Button(Labels.With(Icons.Clapperboard, Str.RenderStart), new Vector2(-1, 0)))
            _report(_ctx.Session.StartFinalRender(s));
        ImGui.PopStyleColor(2);
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(Loc.Get(Str.RenderNotAvailable));
        ImGui.PopStyleColor();
    }
}

/// <summary>
/// The Preview panel (requirement "Превью"): real-time quality settings of all viewports — shadows, volumetric light,
/// indirect light, bloom, tone mapping, exposure and resolution scale (<see cref="PreviewSettings"/>).
/// </summary>
internal sealed class PreviewPanel : Panel
{
    private static readonly string[] ToneMappers = { "AgX", "ACES", "Neutral" };
    private static readonly Str[] IndirectModes = { Str.PreviewIndirectOff, Str.PreviewIndirectRsm, Str.PreviewIndirectRayTraced };
    private readonly WorkspaceContext _ctx;

    /// <summary>Creates the panel.</summary>
    public PreviewPanel(WorkspaceContext context)
        : base("Preview", Icons.Sparkles)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelPreview);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(340, 420);

    /// <inheritdoc/>
    public override void Draw()
    {
        var p = _ctx.Tools.Preview;
        ImGui.SeparatorText(Loc.Get(Str.PreviewQuality));
        if (!PropertyGrid.Begin("##preview", 150f))
            return;
        PropertyGrid.Row(Str.PreviewResolutionScale);
        float resolution = p.ResolutionScale;
        if (ImGui.SliderFloat("##resolution", ref resolution, 0.5f, 1f, "%.2f"))
            p.ResolutionScale = resolution;
        PropertyGrid.Row(Str.PreviewShadows);
        bool shadows = p.Shadows;
        if (ImGui.Checkbox("##shadows", ref shadows))
            p.Shadows = shadows;
        PropertyGrid.Row(Str.PreviewVolumetrics);
        bool volumetrics = p.Volumetrics;
        if (ImGui.Checkbox("##volumetrics", ref volumetrics))
            p.Volumetrics = volumetrics;
        PropertyGrid.Row(Str.PreviewIndirect);
        int indirect = Math.Clamp((int)p.Indirect, 0, IndirectModes.Length - 1);
        if (ImGui.BeginCombo("##indirect", Loc.Get(IndirectModes[indirect])))
        {
            for (int i = 0; i < IndirectModes.Length; i++)
                if (ImGui.Selectable(Loc.Get(IndirectModes[i]), i == indirect))
                    p.Indirect = (IndirectLighting)i;
            ImGui.EndCombo();
        }
        PropertyGrid.Row(Str.PreviewBloom);
        bool bloom = p.Bloom;
        if (ImGui.Checkbox("##bloom", ref bloom))
            p.Bloom = bloom;
        PropertyGrid.Row(Str.PreviewToneMapper);
        int tone = (int)p.ToneMapper;
        if (ImGui.Combo("##tone", ref tone, ToneMappers, ToneMappers.Length))
            p.ToneMapper = (ToneMapper)tone;
        PropertyGrid.Row(Str.PreviewAutoExposure);
        bool auto = p.AutoExposure;
        if (ImGui.Checkbox("##auto-exposure", ref auto))
            p.AutoExposure = auto;
        PropertyGrid.Row(Str.PreviewExposure);
        float ev = p.ExposureCompensation;
        if (ImGui.SliderFloat("##ev", ref ev, -6f, 6f, "%+.1f EV"))
            p.ExposureCompensation = ev;
        PropertyGrid.End();
    }
}
