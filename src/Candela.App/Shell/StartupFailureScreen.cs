using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Ui;
using Hexa.NET.GLFW;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.GLFW;
using Hexa.NET.ImGui.Backends.OpenGL3;

namespace Candela.App.Shell;

/// <summary>
/// The message shown when the editor cannot start its Vulkan renderer (no Vulkan 1.3 device, no driver): a small GLFW
/// window with an OpenGL 3 context drawing a Dear ImGui message box with the reason and what to install. When even that
/// window cannot be created the message goes to the console only.
/// </summary>
internal static class StartupFailureScreen
{
    /// <summary>Prints the message and, when <paramref name="interactive"/>, shows it in a window until closed.</summary>
    public static void Show(string reason, bool interactive, AppLog log)
    {
        string title = Loc.Get(Str.MsgNoVulkanTitle);
        string text = Loc.Get(Str.MsgNoVulkanText);
        string detail = Loc.Format(Str.MsgNoVulkanReason, reason);
        try
        {
            Console.Error.WriteLine($"{title}: {text}");
            Console.Error.WriteLine(detail);
        }
        catch (IOException)
        {
            // No console attached.
        }

        log.Error("Startup", $"{title}. {detail}");
        if (!interactive || !GlfwPlatform.IsInitialized)
            return;
        try
        {
            RunWindow(title, text, detail, log);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            log.Warn("Startup", $"The error window could not be shown: {e.Message}");
        }
    }

    private static unsafe void RunWindow(string title, string text, string detail, AppLog log)
    {
        using var window = MainWindow.Create($"Candela — {title}", 640, 300, forVulkan: false, maximized: false);
        if (window is null)
        {
            log.Warn("Startup", "No OpenGL window is available either; the message was printed to the console.");
            return;
        }

        GLFW.MakeContextCurrent(window.Handle);
        GLFW.SwapInterval(1);
        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        ImGuiImplGLFW.SetCurrentContext(context);
        ImGuiImplOpenGL3.SetCurrentContext(context);
        var io = ImGui.GetIO();
        io.IniFilename = null;
        bool glfwReady = false, glReady = false;
        try
        {
            glfwReady = ImGuiImplGLFW.InitForOpenGL(window.BackendHandle, true);
            glReady = glfwReady && ImGuiImplOpenGL3.Init("#version 130");
            if (!glReady)
            {
                log.Warn("Startup", "OpenGL 3 is not available for the error window.");
                return;
            }

            FontSet.Load(log);
            float scale = UiScale.ClampDpi(window.ContentScale.Y);
            Theme.Apply(1f, scale, FontSet.DefaultSize);
            while (!window.ShouldClose)
            {
                GLFW.WaitEventsTimeout(0.1);
                if (window.IsMinimized)
                    continue;
                ImGuiImplOpenGL3.NewFrame();
                ImGuiImplGLFW.NewFrame();
                ImGui.NewFrame();
                if (Draw(title, text, detail))
                    window.SetShouldClose(true);
                ImGui.Render();
                ImGuiImplOpenGL3.RenderDrawData(ImGui.GetDrawData());
                GLFW.SwapBuffers(window.Handle);
            }
        }
        finally
        {
            if (glReady)
                ImGuiImplOpenGL3.Shutdown();
            if (glfwReady)
                ImGuiImplGLFW.Shutdown();
            ImGui.DestroyContext(context);
        }
    }

    private static bool Draw(string title, string text, string detail)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        bool close = false;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
        if (ImGui.Begin("##startup-failure", flags))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
            ImGui.TextUnformatted($"{Icons.OctagonAlert}  {title}");
            ImGui.PopStyleColor();
            ImGui.Separator();
            ImGui.Spacing();
            SafeText.Wrapped(text);
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            SafeText.Wrapped(detail);
            ImGui.PopStyleColor();
            float buttonWidth = 120f * ImGui.GetStyle().FontScaleDpi;
            ImGui.SetCursorPos(new Vector2(ImGui.GetWindowWidth() - buttonWidth - ImGui.GetStyle().WindowPadding.X,
                ImGui.GetWindowHeight() - ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().WindowPadding.Y));
            close = ImGui.Button(Loc.Get(Str.MsgClose), new Vector2(buttonWidth, 0));
        }

        ImGui.End();
        return close;
    }
}
