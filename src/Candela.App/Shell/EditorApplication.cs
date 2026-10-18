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
    private int _exitCode = ExitCodes.Ok;
    private bool _quitRequested;
    private float _appliedDpi;
    private float _frameDelta;
    private Action? _drawWorkspace;

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
        Loop();
        return _exitCode;
    }

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
        var settings = _settings.Load();
        _settings.ApplyRunOverrides(_options.UiScale, _options.Language, _options.Device);
        Loc.SetLanguage(settings.Language);

        if (!GlfwPlatform.Initialize(_log, out string glfwError))
        {
            _log.Error("Startup", glfwError);
            return ExitCodes.Failure;
        }

        _window = MainWindow.Create(Loc.Get(Str.AppName), _options.Width, _options.Height, forVulkan: true, maximized: false);
        if (_window is null)
        {
            _log.Error("Startup", "The main window could not be created.");
            return ExitCodes.Failure;
        }

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

        _session = new EditorSession(_log, _settings);
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
                // Minimised: nothing to draw; wake up regularly so autosave and protocols keep running.
                GLFW.WaitEventsTimeout(0.1);
                _settings!.Update(_timer.Elapsed);
                continue;
            }

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
                    _exitCode = ExitCodes.Failure;
                break;
            }

            if (attempts > targetFrames + 240)
            {
                _log.Error("Screenshot", "No frame could be presented for the screenshot.");
                _exitCode = ExitCodes.Failure;
                break;
            }
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
            _log.Error("Frame", "Unhandled exception in the frame loop", e);
            _session?.ReportProgramError("app.frame-exception", Loc.Format(Str.MsgUnhandled, "frame", e.Message));
            if (_consecutiveFailures > 120)
            {
                _log.Error("Frame", "Too many consecutive frame failures; the editor stops.");
                _exitCode = ExitCodes.Failure;
                _quitRequested = true;
            }

            return false;
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
        UiFrame.BuildAndRender(_drawWorkspace ??= () => _workspace!.Draw(_frameDelta));
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
            _log.Error("UI", "Rendering a torn-off panel window failed", e);
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
    internal void ReportProgramError(string code, string message) =>
        _session?.Issues.Report(new Issue(IssueSeverity.Error, IssueCategory.Program, code, message));
}
