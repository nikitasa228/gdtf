using System.Net;
using System.Net.Sockets;
using Candela.Cli;
using Candela.Core.Settings;

namespace Candela.Engine.Tests;

/// <summary>The command-line tool without a renderer: usage, exit codes, info, validate, the import worker, Art-Net test.</summary>
public sealed class CliTests
{
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Program.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Usage_errors_and_missing_files_have_their_exit_codes()
    {
        Assert.Equal(Program.ExitUsage, Run().Code);
        Assert.Equal(Program.ExitOk, Run("--help").Code);
        Assert.Equal(Program.ExitUsage, Run("frobnicate").Code);
        Assert.Equal(Program.ExitUsage, Run("render").Code);
        Assert.Equal(Program.ExitUsage, Run("render", "show.mvr").Code);
        Assert.Equal(Program.ExitUsage, Run("render", "show.mvr", "--out", "a.png", "--mode", "x-ray").Code);
        Assert.Equal(Program.ExitUsage, Run("render", "show.mvr", "--out", "a.png", "--camera", "1,2").Code);
        Assert.Equal(Program.ExitUsage, Run("render", "show.mvr", "--out", "a.png", "--camera", "1,2,3").Code); // needs --target
        Assert.Equal(Program.ExitUsage, Run("artnet-test").Code);
        Assert.Equal(Program.ExitUsage, Run("artnet-test", "--to", "not-an-ip").Code);
        Assert.Equal(Program.ExitUsage, Run("artnet-test", "--to", "127.0.0.1", "--universes", "0-3").Code);
        Assert.Equal(Program.ExitUsage, Run("import-worker", "only-one-argument").Code);

        string missing = Path.Combine(TestEnvironment.NewTempDirectory("cli-missing"), "nothing.mvr");
        var render = Run("render", missing, "--out", missing + ".png", "--library", TestEnvironment.Root);
        Assert.Equal(Program.ExitFailure, render.Code);
        Assert.Equal(Program.ExitFailure, Run("info", missing).Code);
        Assert.Equal(Program.ExitFailure, Run("validate", missing, "--library", TestEnvironment.Root).Code);
    }

    [Fact]
    public void Render_options_parse_every_documented_form()
    {
        var o = RenderOptions.Parse(["show.candela", "--out", "x.png", "--width", "640", "--height", "360", "--camera", "1,-10,4", "--target", "0,0,1",
            "--mode", "colorless", "--overlay", "lux", "--haze", "0.05", "--look", "dmx-file", "take.cdmx", "--time", "2.5", "--ev", "8",
            "--frames", "3", "--floor", "none", "--fov", "40"]);
        Assert.Equal("show.candela", o.Scene);
        Assert.Equal(640, o.Width);
        Assert.Equal(new System.Numerics.Vector3(1, -10, 4), o.Camera);
        Assert.Equal(Rendering.ViewShading.Colorless, o.Mode);
        Assert.Equal(Rendering.OverlayMode.Lux, o.Overlay);
        Assert.Equal(0.05f, o.Haze);
        Assert.Equal(RenderLook.DmxFile, o.Look);
        Assert.Equal("take.cdmx", o.Recording);
        Assert.Equal(2.5, o.Time, 6);
        Assert.Equal(8f, o.Ev100);
        Assert.False(o.Floor);

        var defaults = RenderOptions.Parse(["show.mvr", "--out", "x.png", "--view", "top", "--ev", "auto"]);
        Assert.Equal(ViewPreset.Top, defaults.View);
        Assert.Null(defaults.Ev100);
        Assert.Equal(RenderLook.OpenWhite, defaults.Look);
        Assert.True(defaults.Floor);
    }

    [Fact]
    public void Info_and_validate_describe_the_mvr_and_its_issues()
    {
        var info = Run("info", TestEnvironment.Fokina, "--library", TestEnvironment.Root);
        Assert.Equal(Program.ExitOk, info.Code);
        Assert.Contains("91 of 4 type(s)", info.Output, StringComparison.Ordinal);
        Assert.Contains("library substitute", info.Output, StringComparison.Ordinal);

        var gdtf = Run("info", Path.Combine(TestEnvironment.Root, SceneBuilder.MacViper), "--library", TestEnvironment.Root);
        Assert.Equal(Program.ExitOk, gdtf.Code);
        Assert.Contains("MAC Viper Profile", gdtf.Output, StringComparison.Ordinal);
        Assert.Contains("Basic", gdtf.Output, StringComparison.Ordinal);
        Assert.Contains("760000 cd", gdtf.Output, StringComparison.Ordinal);

        var validate = Run("validate", TestEnvironment.Fokina, "--library", TestEnvironment.Root, "--plot");
        Assert.Equal(Program.ExitInputErrors, validate.Code); // the substitutes' footprints overlap
        Assert.Contains("patch.overlap", validate.Output, StringComparison.Ordinal);
        Assert.Contains("Light plot", validate.Output, StringComparison.Ordinal);
        Assert.Contains("Total:", validate.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_worker_writes_a_package_or_the_reason_it_cannot()
    {
        string folder = TestEnvironment.NewTempDirectory("cli-worker");
        string obj = SceneBuilder.WriteCubeObj(folder);
        string output = Path.Combine(folder, "out");
        var ok = Run(ImportWorker.CommandName, obj, output, "--up-axis", "z", "--unit-scale", "0.5", "--no-guess-units");
        Assert.Equal(Program.ExitOk, ok.Code);
        var model = ModelPackage.ReadFile(Path.Combine(output, ImportWorker.PackageFileName));
        Assert.Equal(12, model.TriangleCount);
        Assert.Equal(0.5f, model.Model.ComputeBounds().Size.X, 3);

        string bad = Path.Combine(folder, "bad.dae");
        File.WriteAllText(bad, "<COLLADA><broken");
        string badOut = Path.Combine(folder, "bad-out");
        var rejected = Run(ImportWorker.CommandName, bad, badOut);
        Assert.Equal(Program.ExitInputErrors, rejected.Code);
        Assert.True(File.Exists(Path.Combine(badOut, ImportWorker.ErrorFileName)));
        Assert.False(File.Exists(Path.Combine(badOut, ImportWorker.PackageFileName)));
    }

    [Fact]
    public void Artnet_test_sends_dmx_frames_to_the_target()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var received = Task.Run(() =>
        {
            int dmx = 0;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            listener.Client.ReceiveTimeout = 500;
            while (DateTime.UtcNow < deadline && dmx < 5)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var packet = listener.Receive(ref from);
                    // "Art-Net\0" + OpCode 0x5000 (ArtDmx, little-endian)
                    if (packet.Length > 18 && packet[8] == 0x00 && packet[9] == 0x50) dmx++;
                }
                catch (SocketException)
                {
                    // timeout: keep waiting until the deadline
                }
            }
            return dmx;
        });
        var result = Run("artnet-test", "--to", $"127.0.0.1:{port}", "--universes", "1-2", "--seconds", "0.5", "--fps", "40");
        Assert.Equal(Program.ExitOk, result.Code);
        Assert.Contains("Sent", result.Output, StringComparison.Ordinal);
        Assert.True(received.Result >= 5, $"only {received.Result} ArtDmx packets arrived");
    }

    [Fact]
    public void Engine_options_from_settings_default_to_the_repository_library()
    {
        var options = StageEngineOptions.FromSettings(new AppSettings { ArtNetUniverseOffset = 0, NetworkInterface = "eth0" });
        Assert.Equal(TestEnvironment.Root, Assert.Single(options.LibraryFolders));
        Assert.Equal(0, options.ArtNetUniverseOffset);
        Assert.Equal("eth0", options.NetworkInterface);

        using var engine = new StageEngine(options with { CacheDirectory = TestEnvironment.NewTempDirectory("settings-cache") });
        Assert.True(engine.Library.Count >= 1091);
        Assert.True(engine.Specs.Count >= 1091);

        using var empty = new StageEngine(new StageEngineOptions
        {
            LibraryFolders = [Path.Combine(TestEnvironment.Root, "no-such-folder")],
            CacheDirectory = TestEnvironment.NewTempDirectory("empty-cache"),
        });
        Assert.Contains(empty.Issues.Current, i => i.Code == EngineIssueCodes.LibraryMissing);
        empty.New();
        Assert.Contains(empty.Issues.Current, i => i.Code == EngineIssueCodes.LibraryMissing); // still listed after New
    }
}
