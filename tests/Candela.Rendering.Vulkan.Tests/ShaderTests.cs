using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>Every shader variant compiles with shaderc and passes spirv-val (when the tool is installed).</summary>
public sealed class ShaderTests
{
    [Fact]
    public void EveryShaderVariantCompilesAndValidates()
    {
        string cache = Path.Combine(Path.GetTempPath(), "candela-shader-test-" + Guid.NewGuid().ToString("N"));
        string? spirvVal = FindTool("spirv-val");
        var failures = new List<string>();
        try
        {
            using var compiler = new ShaderCompiler(ShaderLibrary.Default, cache);
            foreach (var variant in ShaderPrograms.All)
            {
                uint[] words;
                try
                {
                    words = compiler.Compile(variant.File, variant.Defines);
                }
                catch (ShaderCompilationException e)
                {
                    failures.Add(e.Message);
                    continue;
                }

                Assert.Equal(0x07230203u, words[0]);
                if (spirvVal is null)
                    continue;
                string path = Path.Combine(cache, $"{variant.File}.{Math.Abs(variant.ToString().GetHashCode()):x}.spv");
                var bytes = new byte[words.Length * 4];
                Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
                File.WriteAllBytes(path, bytes);
                var (code, output) = Run(spirvVal, $"--target-env vulkan1.3 --scalar-block-layout \"{path}\"");
                if (code != 0)
                    failures.Add($"spirv-val {variant}: {output}");
            }

            // Second pass is served from the content-hash cache.
            using var cached = new ShaderCompiler(ShaderLibrary.Default, cache);
            foreach (var variant in ShaderPrograms.All)
                cached.Compile(variant.File, variant.Defines);
            Assert.Equal(ShaderPrograms.All.Count, cached.CacheHits);
            Assert.Equal(0, cached.Compilations);
        }
        finally
        {
            try
            {
                Directory.Delete(cache, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    [Fact]
    public void EveryStageFileIsCoveredByAVariant()
    {
        var used = ShaderPrograms.All.Select(v => v.File).ToHashSet();
        foreach (string file in ShaderLibrary.Default.StageFiles)
            Assert.Contains(file, used);
    }

    [Fact]
    public void GpuStructSizesMatchTheGlslDeclarations()
    {
        Assert.Equal(48, Unsafe.SizeOf<GpuVertex>());
        Assert.Equal(112, Unsafe.SizeOf<GpuMaterial>());
        Assert.Equal(160, Unsafe.SizeOf<GpuInstance>());
        Assert.Equal(224, Unsafe.SizeOf<GpuLight>());
        Assert.Equal(16, Unsafe.SizeOf<GpuLightView>());
        Assert.Equal(192, Unsafe.SizeOf<GpuShadowSlot>());
        Assert.Equal(48, Unsafe.SizeOf<GpuVpl>());
        Assert.Equal(16, Unsafe.SizeOf<GpuExposureResult>());
        Assert.Equal(960, Unsafe.SizeOf<GpuFrameData>());
        Assert.Equal(32, Unsafe.SizeOf<GpuDrawConstants>());
        Assert.Equal(16, Unsafe.SizeOf<GpuClusterCounter>());
        Assert.Equal(32, Unsafe.SizeOf<LineVertex>());
    }

    [Fact]
    public void IncludeChangesInvalidateTheCacheKey()
    {
        var closure = ShaderLibrary.Default.IncludeClosure("forward.frag");
        Assert.Contains("photometry.glsl", closure);
        Assert.Contains("brdf.glsl", closure);
        Assert.Contains("generated.glsl", closure);
    }

    internal static string? FindTool(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            return null;
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            foreach (string candidate in new[] { name, name + ".exe" })
            {
                string full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }

    private static (int Code, string Output) Run(string tool, string arguments)
    {
        var psi = new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
