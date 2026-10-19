using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Input;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Rendering.Vulkan;
using Hexa.NET.GLFW;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.Vulkan;

namespace Candela.App.Shell;

/// <summary>
/// The editor process: start-up (settings, GLFW window, shared Vulkan context + renderer + swapchain, Dear ImGui with
/// docking and multi-viewport, fonts, theme, the editing session and the workspace UI), the frame loop and an orderly
/// shutdown.
/// <para><b>Frame loop</b>: poll events → session update (engine tick) → deferred GPU queries (picking reads the last
/// frame, outside the recorded frame) → ImGui frame (the workspace decides viewport sizes, cameras and edits) →
/// begin the GPU frame → render every viewport into its render target → record ImGui into the swapchain image →
/// present → render the secondary OS windows of torn-off panels. A minimised window idles; a resized window recreates
/// the swapchain. No exception leaves the loop: it is logged, reported in the issues panel and the next frame runs.</para>
/// </summary>
internal sealed class EditorApplication : IDisposable
{
    private static readonly Vector4 ClearColor = new(0.071f, 0.075f, 0.082f, 1f);

    private readonly AppOptions _options;
    private readonly AppLog _log;
    private readonly FrameTimer _timer = new();
    private SettingsStore? _settings;
    private MainWindow? _window;
    private GpuDevice? _gpu;
    private ImGuiHost? _imgui;
    private EditorSession? _session;
    private Workspace? _workspace;
    private SwapchainCapture? _capture;
    private ScriptPlayer? _script;
    private Candela.Core.Geometry.ImageData? _lastCapture;
    private int _lastImageCount;
    private bool _swapchainDirty;
    private int _consecutiveFailures;
    private readonly RepeatedErrorFilter _failures = new();
    private readonly RepeatedErrorFilter _platformFailures = new();
    private int _exitCode = ExitCodes.Ok;
    private bool _quitRequested;
    private float _appliedDpi;
    private float _frameDelta;
    private double _minimizedAt = -1;
    private Action? _drawWorkspace;
    private string? _textLogPath;

    /// <summary>Creates the application for the given command line.</summary>
    public EditorApplication(AppOptions options, AppLog log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Runs the editor until the window closes (or the screenshot is written) and returns the exit code.</summary>
    public int Run()
    {
        int startup = Start();
        if (startup != ExitCodes.Ok)
            return startup;
        Started = true;
        Loop();
        // Only a run that ended normally (the window closed after the unsaved-changes question) deletes the recovery
        // files; a run the frame loop gave up on keeps them for the next start.
        if (_exitCode == ExitCodes.Ok)
            _session?.EndCleanly();
        return _exitCode;
    }

    /// <summary>True once start-up finished (window, GPU, UI and session ready) and the frame loop began.</summary>
    public bool Started { get; private set; }

    /// <summary>Why the run ended with <see cref="ExitCodes.Failure"/> (for the crash report), or null.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Requests the loop to end after the current frame.</summary>
    public void RequestQuit() => _quitRequested = true;

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (!_options.IsScreenshotMode)
                _workspace?.SaveWorkspace();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Warn("Layout", $"The workspace could not be saved: {e.Message}");
        }

        _settings?.SaveNow();
        if (_gpu is not null && !_gpu.Context.IsDisposed)
            _gpu.Context.WaitIdle();
        _workspace?.Dispose();
        _workspace = null;
        _session?.Dispose();
        _session = null;
        _capture?.Dispose();
        _capture = null;
        _imgui?.Dispose();
        _imgui = null;
        _gpu?.Dispose();
        _gpu = null;
        _window?.Dispose();
        _window = null;
        GlfwPlatform.Terminate();
    }

    private int Start()
    {
        // Headless verification runs never write the user's settings or workspace.
        _settings = new SettingsStore(_log) { ReadOnly = _options.IsScreenshotMode };
        bool firstStart = !File.Exists(_settings.FilePath);
        var settings = _settings.Load();
        // The very first start speaks the language of the operating system (stored with the settings from now on).
        if (firstStart && !_options.IsScreenshotMode)
            settings.Language = SystemLanguage.Detect();
        _settings.ApplyRunOverrides(_options.UiScale, _options.Language, _options.Device);
        Loc.SetLanguage(settings.Language);

        if (!GlfwPlatform.Initialize(_log, out string glfwError))
        {
            _log.Error("Startup", glfwError);
            FailureReason = glfwError;
            return ExitCodes.Failure;
        }

        _window = MainWindow.Create(Loc.Get(Str.AppName), _options.Width, _options.Height, forVulkan: true, maximized: false);
        if (_window is null)
        {
            _log.Error("Startup", "The main window could not be created.");
            FailureReason = "The main window could not be created.";
            return ExitCodes.Failure;
        }

        WindowIcon.Apply(_window, _log);

        try
        {
            _gpu = GpuDevice.Create(_window, new GpuDeviceOptions(settings.PreferredGpu, _options.Validation || settings.GpuValidation, true), _log);
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _window.Dispose();
            _window = null;
            StartupFailureScreen.Show(e.Message, interactive: !_options.IsScreenshotMode, _log);
            return ExitCodes.NoVulkan;
        }

        _lastImageCount = _gpu.Swapchain.ImageCount;
        _imgui = ImGuiHost.Create(_window, _gpu, multiViewport: !_options.SingleWindow, _log);
        var fonts = FontSet.Load(_log);
        ApplyScale(force: true);
        _capture = new SwapchainCapture(_gpu.Context);

        // Headless runs neither autosave nor load fixture models in the background (screenshots show every model).
        _session = new EditorSession(_log, _settings, new EditorSessionOptions
        {
            PreviewStage = _options.PreviewStage,
            Autosave = !_options.IsScreenshotMode,
            LoadFixtureModelsInBackground = !_options.IsScreenshotMode,
        });
        _workspace = new Workspace(new WorkspaceContext(_log, _settings, _session, _gpu, _imgui, fonts, _window, this, _timer), _options);

        if (_options.ScriptPath is { } scriptPath)
        {
            try
            {
                _script = new ScriptPlayer(InputScript.Load(scriptPath), _log);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                _log.Error("Script", $"The input script '{scriptPath}' could not be read: {e.Message}");
                return ExitCodes.Usage;
            }
        }

        if (_options.OpenPath is { } open)
            _workspace.OpenAtStartup(open);
        _log.Info("Startup", $"Editor ready ({_gpu.Context.Info.DeviceName}).");
        return ExitCodes.Ok;
    }

    private void Loop()
    {
        var window = _window!;
        bool screenshotMode = _options.IsScreenshotMode;
        int targetFrames = screenshotMode ? Math.Max(_options.Frames, (_script?.FrameCount ?? 0) + 2) : int.MaxValue;
        int frame = 0, attempts = 0;
        while (!_quitRequested)
        {
            GLFW.PollEvents();
            if (window.ShouldClose)
            {
                if (screenshotMode || _workspace!.ConfirmClose())
                    break;
                window.SetShouldClose(false);
            }

            if (window.IsMinimized && !screenshotMode)
            {
                // Minimised: nothing to draw; wake up regularly so autosave, DMX input and the simulation keep running.
                GLFW.WaitEventsTimeout(0.1);
                _settings!.Update(_timer.Elapsed);
                UpdateWhileMinimized();
                continue;
            }

            _minimizedAt = -1;

            _timer.Tick();
            bool capture = screenshotMode && (frame == targetFrames - 1 || _script?.WantsScreenshot(frame) == true);
            bool presented = RunFrameSafely(frame, capture);
            if (!screenshotMode)
            {
                // Interactive runs replay an input script too; the counter stops once the script is done.
                if (presented && _script is { } script && frame <= script.FrameCount)
                    frame++;
                continue;
            }

            attempts++;
            if (presented)
            {
                if (_script?.ScreenshotPath(frame) is { } scriptShot)
                    WriteCapture(scriptShot);
                frame++;
            }

            if (frame >= targetFrames)
            {
                if (!WriteCapture(_options.ScreenshotPath!))
                {
                    _exitCode = ExitCodes.Failure;
                    FailureReason = "The screenshot could not be written.";
                }

                break;
            }

            if (attempts > targetFrames + 240)
            {
                _log.Error("Screenshot", "No frame could be presented for the screenshot.");
                _exitCode = ExitCodes.Failure;
                FailureReason = "No frame could be presented for the screenshot.";
                break;
            }
        }
    }

    /// <summary>
    /// Ticks the session while the window is minimised (no UI, no rendering): the engine keeps evaluating DMX and
    /// autosaving. Failures are logged like frame failures but never end the run.
    /// </summary>
    private void UpdateWhileMinimized()
    {
        double now = _timer.Elapsed;
        double dt = _minimizedAt < 0 ? 0 : Math.Clamp(now - _minimizedAt, 0, FrameTimer.MaxDeltaTime);
        _minimizedAt = now;
        try
        {
            _session?.Update(dt);
        }
        catch (Exception e) when (!IsFatal(e))
        {
            if (_failures.Record(e) == ErrorLogKind.Full)
                _log.Error("Frame", "Updating the session while minimised failed", e);
        }
    }

    private bool RunFrameSafely(int frameIndex, bool capture)
    {
        try
        {
            bool presented = RunFrame(frameIndex, capture);
            _consecutiveFailures = 0;
            return presented;
        }
        catch (Exception e) when (!IsFatal(e))
        {
            _consecutiveFailures++;
            switch (_failures.Record(e))
            {
                case ErrorLogKind.Full:
                    _log.Error("Frame", "Unhandled exception in the frame loop", e);
                    break;
                case ErrorLogKind.Count:
                    _log.Error("Frame", $"The same exception happened {_failures.Repeats} times: {e.GetType().Name}: {e.Message}");
                    break;
            }

            _session?.ReportProgramError("app.frame-exception", Loc.Format(Str.MsgUnhandled, "frame", e.Message));
            if (_consecutiveFailures > 120)
            {
                _log.Error("Frame", "Too many consecutive frame failures; the editor stops.");
                _exitCode = ExitCodes.Failure;
                FailureReason = $"Too many consecutive frame failures; the last one: {e.GetType().Name}: {e.Message}";
                _quitRequested = true;
            }

            return false;
        }
    }

    /// <summary>
    /// Draws the workspace; on a frame a script asked for (<c>uiText</c>) every text the windows draw is appended to the
    /// script's file through Dear ImGui's text log.
    /// </summary>
    private void DrawWorkspace()
    {
        string? log = _textLogPath;
        if (log is null)
        {
            _workspace!.Draw(_frameDelta);
            return;
        }

        try
        {
            // Dear ImGui asserts (aborts the process) when it cannot open the log file, so it must open here first.
            log = Path.GetFullPath(log);
            if (Path.GetDirectoryName(log) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);
            using (new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log.Warn("Script", $"The UI text log '{log}' cannot be written: {e.Message}");
            _workspace!.Draw(_frameDelta);
            return;
        }

        ImGui.LogToFile(-1, log);
        try
        {
            _workspace!.Draw(_frameDelta);
        }
        finally
        {
            ImGui.LogFinish();
        }
    }

    private bool RunFrame(int frameIndex, bool capture)
    {
        var gpu = _gpu!;
        var imgui = _imgui!;
        var workspace = _workspace!;
        float dt = (float)_timer.DeltaTime;

        if (_swapchainDirty || SwapchainSizeChanged())
            RecreateSwapchain();

        ApplyScale(force: false);
        _session!.Update(_timer.DeltaTime);
        workspace.BeforeFrame();
        _script?.Apply(frameIndex, workspace);

        imgui.NewFrame();
        // The frame is ended even when the UI code throws (Dear ImGui aborts the process on an unended frame).
        _frameDelta = dt;
        _textLogPath = _script?.TextLogPath(frameIndex);
        UiFrame.BuildAndRender(_drawWorkspace ??= DrawWorkspace);
        _settings!.Update(_timer.Elapsed);

        var frame = gpu.Context.BeginFrame();
        bool acquired = false, rendering = false;
        try
        {
            workspace.RenderViewports(dt);
            if (gpu.Swapchain.TryAcquire(frame, out var image))
            {
                acquired = true;
                gpu.Swapchain.BeginRendering(frame, ClearColor);
                rendering = true;
                imgui.RenderDrawData(frame.CommandBuffer);
                gpu.Swapchain.EndRendering(frame);
                rendering = false;
                if (capture)
                    _capture!.Record(frame, image);
            }
        }
        catch
        {
            // Keep the frame state machine consistent: close rendering and submit before propagating.
            if (rendering)
                gpu.Swapchain.EndRendering(frame);
            gpu.Swapchain.Present(frame);
            throw;
        }

        if (!gpu.Swapchain.Present(frame))
            _swapchainDirty = true;
        if (_capture!.HasPendingCopy)
            _lastCapture = _capture.Read();

        try
        {
            imgui.RenderPlatformWindows();
        }
        catch (Exception e) when (!IsFatal(e))
        {
            switch (_platformFailures.Record(e))
            {
                case ErrorLogKind.Full:
                    _log.Error("UI", "Rendering a torn-off panel window failed", e);
                    break;
                case ErrorLogKind.Count:
                    _log.Error("UI", $"Rendering a torn-off panel window failed {_platformFailures.Repeats} times: {e.Message}");
                    break;
            }
        }

        workspace.AfterFrame();
        return acquired;
    }

    private bool WriteCapture(string path)
    {
        var image = _lastCapture;
        if (image is null)
        {
            _log.Error("Screenshot", "No frame was captured (the window was minimised or the swapchain unavailable).");
            return false;
        }

        try
        {
            ImageFiles.WritePng(image, path);
            _log.Info("Screenshot", $"Wrote {image.Width} x {image.Height} to '{Path.GetFullPath(path)}'.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Error("Screenshot", $"'{path}' could not be written: {e.Message}");
            return false;
        }
    }

    private bool SwapchainSizeChanged()
    {
        var (w, h) = _window!.FramebufferSize;
        return w > 0 && h > 0 && (w != _gpu!.Swapchain.Width || h != _gpu.Swapchain.Height);
    }

    private void RecreateSwapchain()
    {
        _gpu!.RecreateSwapchain(_window!);
        _swapchainDirty = false;
        if (_gpu.Swapchain.ImageCount != _lastImageCount && _gpu.Swapchain.ImageCount >= 2)
        {
            _lastImageCount = _gpu.Swapchain.ImageCount;
            ImGuiImplVulkan.SetMinImageCount((uint)Math.Max(2, _gpu.Swapchain.MinImageCount));
        }
    }

    /// <summary>Applies the UI scale (setting × monitor content scale) when it changed.</summary>
    private void ApplyScale(bool force)
    {
        var settings = _settings!.Settings;
        float dpi = UiScale.ClampDpi(_window!.ContentScale.Y);
        bool changed = force || Math.Abs(dpi - _appliedDpi) > 1e-3f || (_workspace?.ConsumeStyleDirty() ?? false);
        if (!changed)
            return;
        _appliedDpi = dpi;
        Theme.Apply(settings.UiScale, dpi, settings.FontSize);
    }

    private static bool IsFatal(Exception e) => e is OutOfMemoryException or StackOverflowException or AccessViolationException;

    /// <summary>Reports a program error to the issues panel (used by the workspace for panel failures).</summary>
    internal void ReportProgramError(string code, string message) => _session?.ReportProgramError(code, message);
}
