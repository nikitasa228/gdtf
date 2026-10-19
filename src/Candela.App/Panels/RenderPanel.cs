using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.App.Docking;
using Candela.App.Files;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Dmx.Input;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The Render panel (requirement "Рендер"): final-render settings — output folder, file name and format, resolution
/// (with common sizes), samples per pixel, tone mapper, a single frame or a timecode range of the DMX recording at a
/// frame rate, and the path tracer's bounces — with the preset dropdown (Default / Custom / Save as…) and the
/// <b>Render</b> button (File → Render opens this panel). Final path tracing is not available yet, so the button renders
/// with the real-time renderer at high quality (supersampled and accumulated) and says so; a timecode range renders an
/// image sequence while the engine plays the recording frame by frame.
/// </summary>
internal sealed unsafe class RenderPanel : Panel
{
    private static readonly (string Label, int Width, int Height)[] Sizes =
    {
        ("HD 1280 × 720", 1280, 720),
        ("Full HD 1920 × 1080", 1920, 1080),
        ("QHD 2560 × 1440", 2560, 1440),
        ("4K UHD 3840 × 2160", 3840, 2160),
        ("DCI 2K 2048 × 1080", 2048, 1080),
        ("1080 × 1080", 1080, 1080),
        ("1080 × 1920", 1080, 1920),
    };

    private static readonly string[] ToneMappers = { "AgX", "ACES", "Neutral" };
    private readonly StagePanelContext _stage;
    private readonly Action<SessionResult> _report;
    private readonly byte[] _folder = new byte[1024];
    private readonly byte[] _file = new byte[256];
    private readonly byte[] _start = new byte[32];
    private readonly byte[] _end = new byte[32];
    private readonly LabelColumn _labels = new(RowLabels);
    private PresetCombo<FinalRenderSettings>? _presets;
    private long _shownVersion = -1;
    private bool _range;
    private string _planText = "";
    private (int W, int H, int Spp, string Lang) _planKey;
    private string? _rangeError;
    private string? _folderHint;

    /// <summary>Creates the panel; <paramref name="report"/> shows results in the status bar.</summary>
    public RenderPanel(WorkspaceContext context, Action<SessionResult> report)
        : this(StagePanelContext.From(context ?? throw new ArgumentNullException(nameof(context))), report)
    {
    }

    /// <summary>Creates the panel around a stage context (tests).</summary>
    internal RenderPanel(StagePanelContext stage, Action<SessionResult> report)
        : base("Render", Icons.Clapperboard)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        SetText(_start, "00:00:00:00");
        SetText(_end, "00:00:10:00");
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelRender);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(380, 600);

    /// <summary>True when a timecode range (image sequence) is rendered instead of one frame.</summary>
    internal bool RangeMode
    {
        get => _range;
        set => _range = value;
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        var binding = _stage.Tools.FinalRender;
        var s = binding.Live;
        _presets ??= new PresetCombo<FinalRenderSettings>(binding, "render");
        _presets.Draw();
        if (binding.Version != _shownVersion)
        {
            _shownVersion = binding.Version;
            SetText(_folder, OutputFolder(s.OutputPath));
            SetText(_file, Path.GetFileName(s.OutputPath));
        }

        ImGui.Spacing();
        bool edited = false;
        var job = _stage.Tools.Render;
        bool running = job.State == RenderJobState.Running;
        ImGui.BeginDisabled(running);
        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Image, StageStr.RenderOutput), ImGuiTreeNodeFlags.DefaultOpen) &&
            PropertyGrid.Begin("##render-output", _labels.Width))
        {
            edited |= OutputRows(s);
            PropertyGrid.End();
        }

        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Gauge, StageStr.RenderQuality), ImGuiTreeNodeFlags.DefaultOpen) &&
            PropertyGrid.Begin("##render-quality", _labels.Width))
        {
            edited |= QualityRows(s);
            PropertyGrid.End();
        }

        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Clock, StageStr.RenderTime), ImGuiTreeNodeFlags.DefaultOpen))
            edited |= TimeRows(s);

        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Sparkles, StageStr.RenderPathTracer)))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(StageLoc.Get(StageStr.RenderPathTracerHint));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            if (PropertyGrid.Begin("##render-pt", _labels.Width))
            {
                PropertyGrid.Row(Loc.Get(Str.RenderBounces));
                int bounces = s.MaxBounces;
                if (ImGui.SliderInt("##bounces", ref bounces, 1, 16))
                {
                    s.MaxBounces = bounces;
                    edited = true;
                }

                PropertyGrid.Row(StageLoc.Get(StageStr.RenderVolumeBounces));
                int volume = s.MaxVolumeBounces;
                if (ImGui.SliderInt("##volume-bounces", ref volume, 0, 8))
                {
                    s.MaxVolumeBounces = volume;
                    edited = true;
                }

                PropertyGrid.Row(StageLoc.Get(StageStr.RenderDenoise));
                bool denoise = s.Denoise;
                if (ImGui.Checkbox("##denoise", ref denoise))
                {
                    s.Denoise = denoise;
                    edited = true;
                }

                PropertyGrid.End();
            }
        }

        ImGui.EndDisabled();
        if (edited)
            binding.MarkEdited();

        ImGui.Spacing();
        RenderButton(s, job);
    }

    /// <summary>Every row label of the panel (one label column for all sections).</summary>
    private static IEnumerable<string> RowLabels()
    {
        foreach (var key in new[]
                 {
                     Str.RenderFormat, Str.RenderResolution, Str.RenderSamples, Str.PreviewToneMapper, Str.RenderBounces, Str.RenderRangeStart,
                     Str.RenderRangeEnd, Str.RenderFrameRate,
                 })
            yield return Loc.Get(key);
        foreach (var key in new[] { StageStr.RenderFolder, StageStr.RenderFileName, StageStr.RenderVolumeBounces, StageStr.RenderDenoise, StageStr.RenderRecording })
            yield return StageLoc.Get(key);
    }

    /// <summary>
    /// The folder renders go to when the output path is relative: the user's Pictures folder (else the home folder),
    /// subfolder "Candela".
    /// </summary>
    internal static string DefaultFolder()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures))
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(pictures))
            pictures = Environment.CurrentDirectory;
        return Path.Combine(pictures, "Candela");
    }

    /// <summary>The absolute PNG path of an output path (relative paths go to <see cref="DefaultFolder"/>).</summary>
    internal static string ResolveOutput(string? path)
    {
        string p = string.IsNullOrWhiteSpace(path) ? "render.png" : path.Trim();
        try
        {
            if (!Path.IsPathRooted(p))
                p = Path.Combine(DefaultFolder(), p);
            if (!p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                p = Path.ChangeExtension(p, ".png");
            return Path.GetFullPath(p);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.Combine(DefaultFolder(), "render.png");
        }
    }

    private bool OutputRows(FinalRenderSettings s)
    {
        bool edited = false;
        var style = ImGui.GetStyle();
        PropertyGrid.Row(StageLoc.Get(StageStr.RenderFolder));
        float browse = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(MathF.Max(40f, ImGui.GetContentRegionAvail().X - browse - style.ItemInnerSpacing.X));
        fixed (byte* folder = _folder)
        {
            if (ImGui.InputTextWithHint("##folder", _folderHint ??= DefaultFolder(), folder, (nuint)_folder.Length))
                edited |= ApplyPath(s);
        }

        ImGui.SameLine(0f, style.ItemInnerSpacing.X);
        ImGui.BeginDisabled(_stage.Tools.Browser is null);
        if (ImGui.Button(Icons.FolderOpen + "##browse", new Vector2(browse, 0f)) && _stage.Tools.Browser is { } browser)
        {
            browser.Show(FileBrowserMode.SelectFolder, StageLoc.Get(StageStr.RenderFolder), Array.Empty<FileFilter>(), Text(_folder), null, path =>
            {
                SetText(_folder, path);
                if (ApplyPath(s))
                    _stage.Tools.FinalRender.MarkEdited();
            });
        }

        ImGui.EndDisabled();
        PropertyGrid.Row(StageLoc.Get(StageStr.RenderFileName));
        fixed (byte* file = _file)
        {
            if (ImGui.InputText("##file", file, (nuint)_file.Length))
                edited |= ApplyPath(s);
        }

        PropertyGrid.Row(Loc.Get(Str.RenderFormat));
        if (ImGui.BeginCombo("##format", "PNG"))
        {
            ImGui.Selectable("PNG", true);
            ImGui.BeginDisabled();
            ImGui.Selectable("EXR", false);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(StageLoc.Get(StageStr.RenderExrHint));
            ImGui.EndCombo();
        }

        if (s.Format != ImageFileFormat.Png)
        {
            s.Format = ImageFileFormat.Png;
            edited = true;
        }

        return edited;
    }

    private bool QualityRows(FinalRenderSettings s)
    {
        bool edited = false;
        PropertyGrid.Row(Loc.Get(Str.RenderResolution));
        int* size = stackalloc int[] { s.Width, s.Height };
        var style = ImGui.GetStyle();
        float presets = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(MathF.Max(60f, ImGui.GetContentRegionAvail().X - presets - style.ItemInnerSpacing.X));
        if (ImGui.InputInt2("##resolution", size))
        {
            s.Width = Math.Clamp(size[0], 16, 16384);
            s.Height = Math.Clamp(size[1], 16, 16384);
            edited = true;
        }

        ImGui.SameLine(0f, style.ItemInnerSpacing.X);
        if (ImGui.BeginCombo("##sizes", "", ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft))
        {
            foreach (var (label, w, h) in Sizes)
            {
                if (ImGui.Selectable(label, s.Width == w && s.Height == h))
                {
                    s.Width = w;
                    s.Height = h;
                    edited = true;
                }
            }

            ImGui.EndCombo();
        }

        PropertyGrid.Row(Loc.Get(Str.RenderSamples));
        int spp = s.SamplesPerPixel;
        if (ImGui.SliderInt("##spp", ref spp, 1, 1024, "%d", ImGuiSliderFlags.Logarithmic))
        {
            s.SamplesPerPixel = Math.Clamp(spp, 1, 65536);
            edited = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(PlanText(s));
        PropertyGrid.Row(Loc.Get(Str.PreviewToneMapper));
        int tone = Math.Clamp((int)s.ToneMapper, 0, ToneMappers.Length - 1);
        if (ImGui.Combo("##tone", ref tone, ToneMappers, ToneMappers.Length))
        {
            s.ToneMapper = (ToneMapper)tone;
            edited = true;
        }

        PropertyGrid.Row("");
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(PlanText(s));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
        return edited;
    }

    private bool TimeRows(FinalRenderSettings s)
    {
        bool edited = false;
        if (ImGui.RadioButton(Loc.Get(Str.RenderFrame), !_range))
            _range = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.Get(Str.RenderRange), _range))
            _range = true;
        if (!_range)
            return false;
        var recording = _stage.Tools.External.Recording;
        if (recording is null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(StageLoc.Get(StageStr.RenderNeedsRecording));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        if (PropertyGrid.Begin("##render-range", _labels.Width))
        {
            if (recording is not null)
            {
                PropertyGrid.Row(StageLoc.Get(StageStr.RenderRecording));
                ImGui.TextUnformatted(recording.Name);
            }

            PropertyGrid.Row(Loc.Get(Str.RenderRangeStart));
            fixed (byte* start = _start)
                ImGui.InputText("##start", start, (nuint)_start.Length, ImGuiInputTextFlags.CharsNoBlank);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StageLoc.Get(StageStr.RenderRangeHint));
            PropertyGrid.Row(Loc.Get(Str.RenderRangeEnd));
            fixed (byte* end = _end)
                ImGui.InputText("##end", end, (nuint)_end.Length, ImGuiInputTextFlags.CharsNoBlank);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StageLoc.Get(StageStr.RenderRangeHint));
            PropertyGrid.Row(Loc.Get(Str.RenderFrameRate));
            float fps = (float)s.FrameRate;
            if (ImGui.DragFloat("##fps", ref fps, 0.1f, 1f, 120f, "%.2f fps"))
            {
                s.FrameRate = Math.Clamp(fps, 1f, 240f);
                edited = true;
            }

            PropertyGrid.End();
        }

        if (_rangeError is not null)
            ImGui.TextColored(Theme.Error, _rangeError);
        return edited;
    }

    private void RenderButton(FinalRenderSettings s, RenderJobRunner job)
    {
        bool running = job.State == RenderJobState.Running;
        if (running)
        {
            string progress = string.Create(CultureInfo.InvariantCulture,
                $"{job.FrameIndex + 1}/{job.FrameCount} · {job.PassIndex}/{job.Passes}");
            ImGui.ProgressBar((float)job.Progress, new Vector2(-1f, 0f), progress);
            if (ImGui.Button(StageLoc.With(StageIcons.Stop, StageStr.RenderCancel), new Vector2(-1f, 0f)))
                job.Cancel();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.75f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent);
            if (ImGui.Button(Labels.With(Icons.Clapperboard, Str.RenderStart), new Vector2(-1f, 0f)))
                StartRender(s);
            ImGui.PopStyleColor(2);
        }

        if (job.Message is { } message)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, job.MessageIsError ? Theme.Error : Theme.Success);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(message);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(StageLoc.Get(StageStr.RenderRealtimeNote));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    /// <summary>Starts the render job with the current settings; false (with a message) when it cannot start.</summary>
    internal bool StartRender(FinalRenderSettings s)
    {
        _rangeError = null;
        RenderRange? range = null;
        if (_range)
        {
            var recording = _stage.Tools.External.Recording;
            if (recording is null)
            {
                _rangeError = StageLoc.Get(StageStr.RenderNeedsRecording);
                return false;
            }

            var type = recording.FirstTimecode?.Type ?? TimecodeType.Ebu25;
            if (!TryRangeTime(Text(_start), type, recording, out double start) || !TryRangeTime(Text(_end), type, recording, out double end))
            {
                _rangeError = StageLoc.Get(StageStr.RenderBadRange);
                return false;
            }

            range = new RenderRange(recording, Math.Min(start, end), Math.Max(start, end), s.FrameRate);
        }

        var request = BuildRequest(s, range);
        bool started = _stage.Tools.Render.Start(request, _stage.Engine);
        if (started)
            _report(SessionResult.Done(StageLoc.Format(StageStr.RenderStarted, request.Width, request.Height)));
        return started;
    }

    /// <summary>The request for the current settings, camera (the active viewport) and preview quality.</summary>
    internal RenderRequest BuildRequest(FinalRenderSettings s, RenderRange? range)
    {
        var viewport = _stage.Tools.ActiveViewport;
        var camera = viewport?.Camera.ToRenderCamera() ?? RenderCamera.LookAt(new Vector3(0f, -15f, 6f), new Vector3(0f, 0f, 2f));
        var view = new ViewOptions
        {
            Shading = ViewShading.Full,
            Overlay = OverlayMode.None,
            ShowGrid = false,
            ShowSelectionOutline = false,
            ShowBeamOutlines = false,
            LuxGradient = _stage.Tools.Lux.Live,
            NoiseGradient = _stage.Tools.Noise.Live,
            BackgroundLuminance = viewport?.View.BackgroundLuminance ?? new ViewOptions().BackgroundLuminance,
        };
        var quality = HighQuality(_stage.Tools.Preview.Live, s);
        var (ss, passes) = RenderJobRunner.Plan(s.Width, s.Height, s.SamplesPerPixel);
        return new RenderRequest(camera, view, quality, s.Width, s.Height, ss, passes, ResolveOutput(s.OutputPath), range);
    }

    /// <summary>The real-time settings used for a final frame: the preview look at the highest quality.</summary>
    internal static PreviewSettings HighQuality(PreviewSettings preview, FinalRenderSettings final)
    {
        var q = SettingsCopy.Sanitized(preview);
        q.ResolutionScale = 1f;
        q.Shadows = true;
        q.ShadowMapResolution = Math.Max(q.ShadowMapResolution, 2048);
        q.MaxShadowedLights = 256;
        q.Volumetrics = true;
        q.VolumetricSlices = 256;
        q.MaxRaymarchedBeams = Math.Max(q.MaxRaymarchedBeams, 64);
        q.TemporalAccumulation = true;
        if (q.Indirect == IndirectLighting.Off)
            q.Indirect = IndirectLighting.ReflectiveShadowMaps;
        q.ToneMapper = final.ToneMapper;
        return q;
    }

    /// <summary>A range bound as timecode (HH:MM:SS:FF, converted through the recording's timecodes) or as seconds.</summary>
    internal static bool TryRangeTime(string text, TimecodeType type, Engine.DmxRecording recording, out double seconds)
    {
        seconds = 0;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain) && double.IsFinite(plain) && plain >= 0)
        {
            seconds = Math.Min(plain, recording.Duration);
            return true;
        }

        if (!ExternalControl.TryParseTimecode(text, type, out var timecode))
            return false;
        if (recording.FindTimecode(timecode) is { } found)
        {
            seconds = found;
            return true;
        }

        // A recording without that timecode: count from its first timecode, else from the start.
        double offset = recording.FirstTimecode is { } first ? timecode.TotalSeconds - first.TotalSeconds : timecode.TotalSeconds;
        if (!(offset >= 0) || offset > recording.Duration + 1e-6)
            return false;
        seconds = offset;
        return true;
    }

    private string PlanText(FinalRenderSettings s)
    {
        var key = (s.Width, s.Height, s.SamplesPerPixel, Loc.Language);
        if (key != _planKey || _planText.Length == 0)
        {
            _planKey = key;
            var (ss, passes) = RenderJobRunner.Plan(s.Width, s.Height, s.SamplesPerPixel);
            _planText = StageLoc.Format(StageStr.RenderPlan, ss, passes, s.Width, s.Height);
        }

        return _planText;
    }

    private bool ApplyPath(FinalRenderSettings s)
    {
        string folder = Text(_folder).Trim();
        string file = Text(_file).Trim();
        if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        string path;
        try
        {
            path = folder.Length == 0 ? file : Path.Combine(folder, file);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (path == s.OutputPath)
            return false;
        s.OutputPath = path;
        return true;
    }

    private static string OutputFolder(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(dir) ? "" : dir;
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private static void SetText(byte[] buffer, string text)
    {
        Array.Clear(buffer);
        var encoder = Encoding.UTF8.GetEncoder();
        encoder.Convert(text.AsSpan(), buffer.AsSpan(0, buffer.Length - 1), flush: true, out _, out _, out _);
    }

    private static string Text(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }
}
