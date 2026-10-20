using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.App.Docking;
using Candela.App.Files;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Thumbnails;
using Candela.App.Ui;
using Candela.Dmx.Input;
using Candela.Engine;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The Render panel (requirement "Рендер"): the final render of the active viewport's camera — output folder, file name
/// and format (PNG, or PNG + linear HDR OpenEXR), resolution (with common sizes), samples per pixel, tone mapper, work
/// light, a single frame or a timecode range of the DMX recording at a frame rate, and the path tracer's surface
/// bounces, volume bounces (scattering in haze and smoke) and denoiser — with the preset dropdown (Default / Custom /
/// Save as…). <b>Render</b>, File → Render and F12 start the render with these settings
/// (<see cref="StartFromCommand"/>): the path tracer by default (<see cref="RenderMode.PathTraced"/>, the same light
/// transport and files as <c>candela render --final</c>), or the real-time renderer at its highest quality
/// (<see cref="RenderMode.Fast"/>; also the fall-back on a GPU without ray queries, which the panel says). While it runs
/// the panel shows the progress (frame, samples or passes, time left), the latest image, Cancel and — path traced — Stop
/// and save; afterwards the result and an Open folder button.
/// </summary>
internal sealed unsafe class RenderPanel : Panel, IDisposable
{
    /// <summary>Largest bounce count the sliders offer (Ctrl+click types up to <see cref="VulkanFinalRender.MaxBounceLimit"/>).</summary>
    public const int BounceSliderMax = 16;

    /// <summary>Largest volume-bounce count the slider offers (Ctrl+click types up to <see cref="VulkanFinalRender.MaxBounceLimit"/>).</summary>
    public const int VolumeBounceSliderMax = 8;

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
    private readonly UiTextures _textures = new();
    private PresetCombo<FinalRenderSettings>? _presets;
    private long _shownVersion = -1;
    private bool _range;
    private RenderMode _mode = RenderMode.PathTraced;
    private WorkLightMode _workLight = WorkLightMode.Off;
    private string _planText = "";
    private (int W, int H, int Spp, string Lang, RenderMode Mode, bool Exr, bool RayTracing) _planKey;
    private string _progressText = "";
    private (int Frame, int Count, int Done, int Total, bool Preparing, RenderMode Mode, string Lang) _progressKey;
    private string _timeText = "";
    private (long Seconds, int Permille, string Lang) _timeKey = (-1, -1, "");
    private string? _rangeError;
    private string? _folderHint;
    private UiTexture? _preview;
    private int _previewVersion = -1;
    private (int W, int H) _previewSize;
    private bool _disposed;

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
    public override Vector2 DefaultSize => new(380, 640);

    /// <summary>True when a timecode range (image sequence) is rendered instead of one frame.</summary>
    internal bool RangeMode
    {
        get => _range;
        set => _range = value;
    }

    /// <summary>
    /// The renderer of the next render: the path tracer (default) or the real-time Fast mode. Kept for the session, not
    /// with the preset; a path-traced render on a GPU without ray queries falls back to Fast and says so.
    /// </summary>
    internal RenderMode Mode
    {
        get => _mode;
        set => _mode = Enum.IsDefined(value) ? value : RenderMode.PathTraced;
    }

    /// <summary>
    /// The work light of the next render (<see cref="RenderRequest.WorkLight"/>): <see cref="WorkLightMode.Off"/> unless
    /// the user asks for it in this panel; kept for the session, not with the preset.
    /// </summary>
    internal WorkLightMode WorkLight
    {
        get => _workLight;
        set => _workLight = Enum.IsDefined(value) ? value : WorkLightMode.Off;
    }

    /// <summary>
    /// Opens a folder in the system's file manager; returns null on success or the reason it could not. Replaced by tests
    /// (the default starts the shell's handler of the folder).
    /// </summary>
    internal Func<string, string?> OpenFolderHandler { get; set; } = OpenInFileManager;

    /// <summary>True when the path tracer cannot run on the editor's GPU (no ray queries): renders use the Fast mode.</summary>
    internal bool PathTracerUnavailable => _stage.Tools.RendererInfo is { SupportsRayTracing: false };

    /// <summary>The text of the last progress line drawn (tests).</summary>
    internal string ProgressText => _progressText;

    /// <inheritdoc/>
    public override void Draw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _textures.Update();
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

        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Sparkles, StageStr.RenderPathTracer), ImGuiTreeNodeFlags.DefaultOpen))
            edited |= PathTracerRows(s);

        ImGui.EndDisabled();
        if (edited)
            binding.MarkEdited();

        ImGui.Spacing();
        JobRows(job);
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
        foreach (var key in new[]
                 {
                     StageStr.RenderFolder, StageStr.RenderFileName, StageStr.RenderVolumeBounces, StageStr.RenderDenoise, StageStr.RenderRecording,
                     StageStr.RenderWorkLight, StageStr.RenderMode,
                 })
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

        // PNG always; EXR (linear HDR) next to it from the path tracer. The choice is kept with the preset even while the
        // Fast mode is selected (which writes the PNG only).
        PropertyGrid.Row(Loc.Get(Str.RenderFormat));
        bool exrPossible = _mode == RenderMode.PathTraced && !PathTracerUnavailable;
        string exrLabel = StageLoc.Get(StageStr.RenderExr);
        if (ImGui.BeginCombo("##format", s.Format == ImageFileFormat.Exr && exrPossible ? exrLabel : "PNG"))
        {
            if (ImGui.Selectable("PNG", s.Format != ImageFileFormat.Exr || !exrPossible) && s.Format != ImageFileFormat.Png)
            {
                s.Format = ImageFileFormat.Png;
                edited = true;
            }

            ImGui.BeginDisabled(!exrPossible);
            if (ImGui.Selectable(exrLabel, s.Format == ImageFileFormat.Exr && exrPossible) && s.Format != ImageFileFormat.Exr)
            {
                s.Format = ImageFileFormat.Exr;
                edited = true;
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                SafeText.Tooltip(StageLoc.Get(StageStr.RenderExrHint));
            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            SafeText.Tooltip(StageLoc.Get(StageStr.RenderExrHint));
        return edited;
    }

    private bool QualityRows(FinalRenderSettings s)
    {
        bool edited = false;
        PropertyGrid.Row(StageLoc.Get(StageStr.RenderMode));
        if (ImGui.BeginCombo("##mode", StageLoc.Get(ModeLabel(_mode))))
        {
            foreach (var mode in new[] { RenderMode.PathTraced, RenderMode.Fast })
            {
                if (ImGui.Selectable(StageLoc.Get(ModeLabel(mode)), _mode == mode))
                    _mode = mode;
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            SafeText.Tooltip(StageLoc.Get(StageStr.RenderModeHint));
        if (_mode == RenderMode.PathTraced && PathTracerUnavailable)
        {
            PropertyGrid.Row("");
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(StageLoc.Format(StageStr.RenderNoRayTracing, _stage.Tools.RendererInfo?.DeviceName ?? "GPU"));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        PropertyGrid.Row(Loc.Get(Str.RenderResolution));
        int* size = stackalloc int[] { s.Width, s.Height };
        var style = ImGui.GetStyle();
        float presets = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(MathF.Max(60f, ImGui.GetContentRegionAvail().X - presets - style.ItemInnerSpacing.X));
        if (ImGui.InputInt2("##resolution", size))
        {
            s.Width = Math.Clamp(size[0], 16, RenderJobRunner.MaxFinalSide);
            s.Height = Math.Clamp(size[1], 16, RenderJobRunner.MaxFinalSide);
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
        if (ImGui.SliderInt("##spp", ref spp, 1, 4096, "%d", ImGuiSliderFlags.Logarithmic))
        {
            s.SamplesPerPixel = Math.Clamp(spp, 1, VulkanFinalRender.MaxSamples);
            edited = true;
        }

        if (ImGui.IsItemHovered())
            SafeText.Tooltip(PlanText(s));
        PropertyGrid.Row(Loc.Get(Str.PreviewToneMapper));
        int tone = Math.Clamp((int)s.ToneMapper, 0, ToneMappers.Length - 1);
        if (ImGui.Combo("##tone", ref tone, ToneMappers, ToneMappers.Length))
        {
            s.ToneMapper = (ToneMapper)tone;
            edited = true;
        }

        // Asked per render: a final picture shows the stage light only unless the work light is chosen here.
        PropertyGrid.Row(StageLoc.Get(StageStr.RenderWorkLight));
        if (WorkLightCombo.Draw("##render-work-light", _workLight, out var workLight))
            _workLight = workLight;
        if (ImGui.IsItemHovered())
            SafeText.Tooltip(StageLoc.Get(StageStr.RenderWorkLightHint));

        PropertyGrid.Row("");
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(PlanText(s));
        if (EffectiveMode == RenderMode.Fast)
            ImGui.TextUnformatted(StageLoc.Get(StageStr.RenderFastNote));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
        return edited;
    }

    private bool PathTracerRows(FinalRenderSettings s)
    {
        bool edited = false;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(StageLoc.Get(StageStr.RenderPathTracerHint));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
        ImGui.BeginDisabled(EffectiveMode != RenderMode.PathTraced);
        if (PropertyGrid.Begin("##render-pt", _labels.Width))
        {
            PropertyGrid.Row(Loc.Get(Str.RenderBounces));
            int bounces = s.MaxBounces;
            if (ImGui.SliderInt("##bounces", ref bounces, 0, BounceSliderMax))
            {
                s.MaxBounces = Math.Clamp(bounces, 0, VulkanFinalRender.MaxBounceLimit);
                edited = true;
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                SafeText.Tooltip(StageLoc.Get(StageStr.RenderBouncesHint));
            PropertyGrid.Row(StageLoc.Get(StageStr.RenderVolumeBounces));
            int volume = s.MaxVolumeBounces;
            if (ImGui.SliderInt("##volume-bounces", ref volume, 0, VolumeBounceSliderMax))
            {
                s.MaxVolumeBounces = Math.Clamp(volume, 0, VulkanFinalRender.MaxBounceLimit);
                edited = true;
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                SafeText.Tooltip(StageLoc.Get(StageStr.RenderVolumeBouncesHint));
            PropertyGrid.Row(StageLoc.Get(StageStr.RenderDenoise));
            bool denoise = s.Denoise;
            if (ImGui.Checkbox("##denoise", ref denoise))
            {
                s.Denoise = denoise;
                edited = true;
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                SafeText.Tooltip(StageLoc.Get(StageStr.RenderDenoiseHint));
            PropertyGrid.End();
        }

        ImGui.EndDisabled();
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
                SafeText.Tooltip(StageLoc.Get(StageStr.RenderRangeHint));
            PropertyGrid.Row(Loc.Get(Str.RenderRangeEnd));
            fixed (byte* end = _end)
                ImGui.InputText("##end", end, (nuint)_end.Length, ImGuiInputTextFlags.CharsNoBlank);
            if (ImGui.IsItemHovered())
                SafeText.Tooltip(StageLoc.Get(StageStr.RenderRangeHint));
            PropertyGrid.Row(Loc.Get(Str.RenderFrameRate));
            float fps = (float)s.FrameRate;
            if (ImGui.DragFloat("##fps", ref fps, 0.1f, 1f, 120f, StageUnits.Current.FormatFramesPerSecond))
            {
                s.FrameRate = Math.Clamp(fps, 1f, 240f);
                edited = true;
            }

            PropertyGrid.End();
        }

        if (_rangeError is not null)
            SafeText.Colored(Theme.Error, _rangeError);
        return edited;
    }

    /// <summary>The Render button, or the running job's progress, Cancel and Stop and save; the message, the picture and Open folder.</summary>
    private void JobRows(RenderJobRunner job)
    {
        bool running = job.State == RenderJobState.Running;
        if (running)
        {
            ImGui.ProgressBar((float)job.Progress, new Vector2(-1f, 0f), ProgressLine(job));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.TextUnformatted(TimeLine(job));
            ImGui.PopStyleColor();
            bool canSave = job.Mode == RenderMode.PathTraced && job.SamplesDone > 0;
            float width = ImGui.GetContentRegionAvail().X;
            float half = (width - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            if (ImGui.Button(StageLoc.With(StageIcons.Stop, StageStr.RenderCancel), new Vector2(canSave ? half : -1f, 0f)))
                job.Cancel();
            if (canSave)
            {
                ImGui.SameLine();
                if (ImGui.Button(StageLoc.With(Icons.Save, StageStr.RenderStopSave), new Vector2(-1f, 0f)))
                    job.StopAndSave();
                if (ImGui.IsItemHovered())
                    SafeText.Tooltip(StageLoc.Get(StageStr.RenderStopSaveHint));
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.75f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent);
            if (ImGui.Button(Labels.With(Icons.Clapperboard, Str.RenderStart), new Vector2(-1f, 0f)))
                StartRender(_stage.Tools.FinalRender.Live);
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

        string? folder = ResultFolder(job);
        ImGui.BeginDisabled(folder is null);
        if (ImGui.Button(StageLoc.With(Icons.FolderOpen, StageStr.RenderOpenFolder)) && folder is not null)
            OpenFolder(folder);
        ImGui.EndDisabled();
        PreviewRows(job);
    }

    /// <summary>The latest picture of the job (progress image or written frame), scaled to the panel's width.</summary>
    private void PreviewRows(RenderJobRunner job)
    {
        if (job.PreviewImage is not { } image)
            return;
        if (job.PreviewVersion != _previewVersion)
        {
            _previewVersion = job.PreviewVersion;
            _textures.Release(_preview);
            _preview = _textures.Create(image);
            _previewSize = (image.Width, image.Height);
        }

        ImGui.Spacing();
        ImGui.SeparatorText(StageLoc.Get(StageStr.RenderPreview));
        var (w, h) = _previewSize;
        if (_preview is { IsReleased: false } texture && w > 0 && h > 0)
        {
            float available = MathF.Max(32f, ImGui.GetContentRegionAvail().X);
            float scale = MathF.Min(available / w, 1.5f * ImGui.GetFontSize() * 24f / h);
            ImGui.Image(texture.Ref, new Vector2(w * scale, h * scale));
        }
        else
        {
            SafeText.Disabled(StageLoc.Format(StageStr.RenderPreviewHeadless, w, h));
        }
    }

    /// <summary>"Frame 1/1 · 12/64 samples" (or passes in Fast mode, or the wait for fixture models); rebuilt only when it changes.</summary>
    private string ProgressLine(RenderJobRunner job)
    {
        bool traced = job.Mode == RenderMode.PathTraced;
        var key = (job.FrameIndex, job.FrameCount, traced ? job.SamplesDone : job.PassIndex, traced ? job.SamplesTotal : job.Passes,
            job.IsPreparing, job.Mode, Loc.Language);
        if (key == _progressKey && _progressText.Length > 0)
            return _progressText;
        _progressKey = key;
        int frame = Math.Min(job.FrameIndex + 1, Math.Max(job.FrameCount, 1));
        _progressText = job.IsPreparing
            ? StageLoc.Get(StageStr.RenderPreparing)
            : StageLoc.Format(traced ? StageStr.RenderProgressSamples : StageStr.RenderProgressPasses, frame, job.FrameCount, key.Item3, key.Item4);
        return _progressText;
    }

    /// <summary>"0:42 elapsed, about 1:10 left" (rebuilt once a second).</summary>
    private string TimeLine(RenderJobRunner job)
    {
        var elapsed = job.Elapsed;
        double progress = job.Progress;
        var key = ((long)elapsed.TotalSeconds, (int)(progress * 1000), Loc.Language);
        if (key == _timeKey)
            return _timeText;
        _timeKey = key;
        _timeText = progress is > 0.01 and < 1
            ? StageLoc.Format(StageStr.RenderElapsedLeft, FormatDuration(elapsed), FormatDuration(elapsed * ((1 - progress) / progress)))
            : StageLoc.Format(StageStr.RenderElapsed, FormatDuration(elapsed));
        return _timeText;
    }

    /// <summary>A duration as m:ss or h:mm:ss.</summary>
    internal static string FormatDuration(TimeSpan time)
    {
        if (!(time.TotalSeconds >= 0) || time.TotalDays > 99)
            return "—";
        long seconds = (long)Math.Round(time.TotalSeconds);
        return seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>The folder Open folder shows: where the last file was written, else the output folder when it exists.</summary>
    internal string? ResultFolder(RenderJobRunner job)
    {
        string? path = job.LastOutput ?? job.OutputPath;
        try
        {
            path ??= ResolveOutput(_stage.Tools.FinalRender.Live.OutputPath);
            string? folder = Path.GetDirectoryName(path);
            return folder is not null && Directory.Exists(folder) ? folder : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>Opens <paramref name="folder"/> in the file manager; a failure is reported in the status bar.</summary>
    internal bool OpenFolder(string folder)
    {
        string? error = OpenFolderHandler(folder);
        if (error is null)
            return true;
        _report(SessionResult.Failed(StageLoc.Format(StageStr.RenderOpenFolderFailed, folder, error)));
        return false;
    }

    /// <summary>Opens a folder with the shell (Explorer, Finder, the desktop's file manager via xdg-open).</summary>
    private static string? OpenInFileManager(string folder)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or
                                       FileNotFoundException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// File → Render and F12: starts a render with the panel's current settings (the live preset, renderer, frame or
    /// range, work light). False when a render is already running (it goes on) or the request cannot start (the reason
    /// is in the panel and the status bar).
    /// </summary>
    internal bool StartFromCommand()
    {
        if (_stage.Tools.Render.State == RenderJobState.Running)
            return false;
        return StartRender(_stage.Tools.FinalRender.Live);
    }

    /// <summary>Starts the render job with the current settings; false (with a message) when it cannot start.</summary>
    internal bool StartRender(FinalRenderSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
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

        // A request that cannot start says why in the panel (the job's message), which File → Render and F12 bring to
        // the front; only a start is reported in the status bar.
        var request = BuildRequest(s, range);
        bool started = _stage.Tools.Render.Start(request, _stage.Engine);
        if (started)
            _report(SessionResult.Done(StageLoc.Format(StageStr.RenderStarted, request.Width, request.Height)));
        return started;
    }

    /// <summary>
    /// The request for the current settings, camera (the active viewport, its exposure) and preview look: the renderer
    /// (<see cref="Mode"/>), the path tracer's settings and options — auto exposure, metering and compensation of the
    /// Preview panel, the viewport's background — and PNG + EXR when chosen.
    /// </summary>
    internal RenderRequest BuildRequest(FinalRenderSettings s, RenderRange? range)
    {
        ArgumentNullException.ThrowIfNull(s);
        var viewport = _stage.Tools.ActiveViewport;
        var camera = viewport?.Camera.ToRenderCamera() ?? RenderCamera.LookAt(new Vector3(0f, -15f, 6f), new Vector3(0f, 0f, 2f));
        float background = viewport?.View.BackgroundLuminance ?? new ViewOptions().BackgroundLuminance;
        var view = new ViewOptions
        {
            Shading = ViewShading.Full,
            Overlay = OverlayMode.None,
            ShowGrid = false,
            ShowSelectionOutline = false,
            ShowBeamOutlines = false,
            LuxGradient = _stage.Tools.Lux.Live,
            NoiseGradient = _stage.Tools.Noise.Live,
            BackgroundLuminance = background,
        };
        var preview = _stage.Tools.Preview.Live;
        var quality = HighQuality(preview, s);
        var (ss, passes) = RenderJobRunner.Plan(s.Width, s.Height, s.SamplesPerPixel);
        var final = SettingsCopy.Sanitized(s);
        final.Width = s.Width;
        final.Height = s.Height;
        return new RenderRequest(camera, view, quality, s.Width, s.Height, ss, passes, ResolveOutput(s.OutputPath), range)
        {
            WorkLight = _workLight,
            Mode = _mode,
            Final = final,
            FinalOptions = new FinalRenderOptions
            {
                AutoExposure = quality.AutoExposure,
                Metering = quality.Metering,
                ExposureCompensation = quality.ExposureCompensation,
                BackgroundLuminance = background,
            },
            WriteExr = s.Format == ImageFileFormat.Exr,
        };
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
        q.MaxRaymarchedBeams = SettingsCopy.MaxVolumetricBeams;
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

    /// <summary>The renderer a render started now would use: Fast when chosen or when the GPU has no ray queries.</summary>
    private RenderMode EffectiveMode => _mode == RenderMode.PathTraced && !PathTracerUnavailable ? RenderMode.PathTraced : RenderMode.Fast;

    private static StageStr ModeLabel(RenderMode mode) => mode == RenderMode.PathTraced ? StageStr.RenderModePathTraced : StageStr.RenderModeFast;

    /// <summary>What the next render does: samples and files (path traced) or supersampling and frames (Fast).</summary>
    internal string PlanText(FinalRenderSettings s)
    {
        var mode = EffectiveMode;
        bool exr = mode == RenderMode.PathTraced && s.Format == ImageFileFormat.Exr;
        var key = (s.Width, s.Height, s.SamplesPerPixel, Loc.Language, mode, exr, !PathTracerUnavailable);
        if (key != _planKey || _planText.Length == 0)
        {
            _planKey = key;
            if (mode == RenderMode.PathTraced)
            {
                _planText = StageLoc.Format(StageStr.RenderPlanFinal, Math.Clamp(s.SamplesPerPixel, 1, VulkanFinalRender.MaxSamples), s.Width, s.Height,
                    exr ? StageLoc.Get(StageStr.RenderExr) : "PNG");
            }
            else
            {
                var (ss, passes) = RenderJobRunner.Plan(s.Width, s.Height, s.SamplesPerPixel);
                _planText = StageLoc.Format(StageStr.RenderPlan, ss, passes, s.Width, s.Height);
            }
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

    /// <summary>Releases the preview texture (before the Dear ImGui backend shuts down).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _preview = null;
        _textures.Dispose();
    }
}
