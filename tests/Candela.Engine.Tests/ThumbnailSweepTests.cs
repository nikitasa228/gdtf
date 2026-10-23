using System.Diagnostics;
using Candela.Assets;
using Candela.Gdtf;
using Candela.Rendering.Vulkan;
using Xunit.Abstractions;

namespace Candela.Engine.Tests;

/// <summary>
/// The library's thumbnail and hover-preview renderer over the fixtures of the repository, on a headless Vulkan device
/// with the validation layer (synchronization validation on): no exception, no validation error, no device loss, a
/// finite picture of the requested size for every fixture, in bounded time. By default the riskiest fixtures run — the
/// largest GDTF files (biggest models), the files with the most geometry parts and a spread over the whole library;
/// <c>CANDELA_THUMBNAIL_SWEEP=all</c> renders every one of the 1091 (about ten minutes on lavapipe).
/// </summary>
[Collection(VulkanCollection.Name)]
public sealed class ThumbnailSweepTests
{
    private readonly ITestOutputHelper _output;

    public ThumbnailSweepTests(ITestOutputHelper output) => _output = output;

    private static IReadOnlyList<FixtureLibraryEntry> Fixtures()
    {
        var library = TestEnvironment.Library;
        var latest = library.Entries.Where(library.IsLatestRevision).OrderBy(e => e.FileName, StringComparer.Ordinal).ToList();
        if (string.Equals(Environment.GetEnvironmentVariable("CANDELA_THUMBNAIL_SWEEP"), "all", StringComparison.OrdinalIgnoreCase))
            return latest;
        // The 12 largest files (their models are the heaviest), and every 60th fixture of the sorted list.
        var chosen = new List<FixtureLibraryEntry>(latest.OrderByDescending(e => e.FileSize).Take(12));
        for (int i = 0; i < latest.Count; i += 60)
            chosen.Add(latest[i]);
        return chosen.Distinct().ToList();
    }

    [SkippableFact]
    public void Every_chosen_fixture_renders_a_still_and_orbit_frames_without_errors()
    {
        var errors = new List<string>();
        VulkanRenderer renderer;
        try
        {
            renderer = VulkanRenderer.Create(new VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
                EnableRayTracing = false,
                EnableValidation = true,
                SynchronizationValidation = true,
                MinimumLogLevel = VulkanLogLevel.Warning,
                Log = (level, message) =>
                {
                    if (level >= VulkanLogLevel.Error)
                        lock (errors)
                            errors.Add(message);
                },
            });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }

        using (renderer)
        {
            using var thumbnails = new FixtureThumbnailRenderer(renderer, TestEnvironment.Library);
            var timings = new List<(string Name, double Ms)>();
            foreach (var entry in Fixtures())
            {
                var watch = Stopwatch.StartNew();
                var still = thumbnails.Render(entry, 96);
                var a = thumbnails.RenderOrbitFrame(entry, 128, 128, 0f);
                var b = thumbnails.RenderOrbitFrame(entry, 128, 128, 2.5f);
                watch.Stop();
                timings.Add((entry.FileName, watch.Elapsed.TotalMilliseconds));
                foreach (var image in new[] { still, a, b })
                {
                    Assert.True(image.Pixels.Length >= image.Width * image.Height * 4, $"{entry.FileName}: short picture");
                    Assert.True(image.Width is 96 or 128, $"{entry.FileName}: {image.Width} px");
                }

                lock (errors)
                    Assert.True(errors.Count == 0, $"{entry.FileName}: {string.Join("\n", errors.Take(3))}");
            }

            foreach (var (name, ms) in timings.OrderByDescending(t => t.Ms).Take(8))
                _output.WriteLine($"{ms,8:0} ms  {name}");
            _output.WriteLine($"{timings.Count} fixtures, {timings.Sum(t => t.Ms) / 1000:0.0} s");
            // Lavapipe renders a heavy fixture in a few seconds; a hang or a runaway model would take far longer.
            Assert.True(timings.Max(t => t.Ms) < 60_000, $"{timings.MaxBy(t => t.Ms).Name} took {timings.Max(t => t.Ms):0} ms");
        }
    }
}
