using Candela.App.Diagnostics;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Engine;
using Candela.Gdtf;
using Candela.Rendering.Vulkan;

namespace Candela.App.Thumbnails;

/// <summary>What a thumbnail request asks for.</summary>
internal enum ThumbnailKind
{
    /// <summary>The still picture of a fixture type (cached on disk in the thumbnail store).</summary>
    FixtureStill,

    /// <summary>One frame of a fixture's rotating hover preview (not cached on disk).</summary>
    FixtureOrbit,

    /// <summary>A downscaled picture of an image file of the model library.</summary>
    Image,
}

/// <summary>A picture the library panel wants.</summary>
/// <param name="Key">Identity of the result (the panel's texture cache key).</param>
/// <param name="Kind">What to produce.</param>
/// <param name="Entry">The fixture type (fixture kinds).</param>
/// <param name="Path">The image file (images).</param>
/// <param name="Size">Edge length in pixels (square).</param>
/// <param name="Azimuth">Camera azimuth for orbit frames, rad.</param>
internal sealed record ThumbnailRequest(string Key, ThumbnailKind Kind, FixtureLibraryEntry? Entry, string? Path, int Size, float Azimuth = 0f);

/// <summary>A finished picture (null image: it could not be made; the panel shows an icon instead).</summary>
internal sealed record ThumbnailResult(string Key, ImageData? Image);

/// <summary>
/// Makes the library's pictures on a background thread so that drawing the panel never waits for them: fixture
/// thumbnails and hover-preview frames are rendered by Candela.Engine's <see cref="FixtureThumbnailRenderer"/> with a
/// Vulkan renderer of the worker's own (a second, window-less device on the same GPU, created on first use; still
/// thumbnails are cached on disk in a <see cref="ThumbnailStore"/> keyed by the GDTF file's content), and image files are
/// decoded and downscaled. The panel states what it wants each frame (<see cref="SetWanted"/>, most important first);
/// requests that are no longer wanted are dropped before they start, so scrolling quickly through 1000 fixtures only
/// renders what stays on screen.
/// </summary>
internal sealed class ThumbnailWorker : IDisposable
{
    /// <summary>Pause between two renders, so a software renderer leaves the main thread its share of the CPU.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(4);

    private readonly object _gate = new();
    private readonly List<ThumbnailRequest> _wanted = new();
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly Queue<ThumbnailResult> _results = new();
    private readonly FixtureLibrary _library;
    private readonly ThumbnailStore? _store;
    private readonly AppLog _log;
    private readonly string _deviceName;
    private readonly string? _cacheDirectory;
    private readonly Thread _thread;
    private bool _stop;
    private string? _current;
    private volatile string? _error;

    /// <summary>
    /// Starts the worker over <paramref name="library"/>. <paramref name="deviceName"/> picks the GPU of the editor;
    /// <paramref name="store"/> caches still thumbnails (null: none).
    /// </summary>
    public ThumbnailWorker(FixtureLibrary library, ThumbnailStore? store, AppLog log, string deviceName, string? cacheDirectory = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _store = store;
        _deviceName = deviceName ?? "";
        _cacheDirectory = cacheDirectory;
        _thread = new Thread(Run) { IsBackground = true, Name = "Candela thumbnails", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Why fixture pictures cannot be rendered (no second Vulkan device), or null.</summary>
    public string? Error => _error;

    /// <summary>Requests waiting (not counting the one being made).</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
                return _wanted.Count;
        }
    }

    /// <summary>
    /// Replaces the wanted list (most important first). Results already made, and the request being made, are not
    /// requested again.
    /// </summary>
    public void SetWanted(IReadOnlyList<ThumbnailRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        lock (_gate)
        {
            _wanted.Clear();
            foreach (var r in requests)
            {
                if (!_done.Contains(r.Key) && !string.Equals(r.Key, _current, StringComparison.Ordinal))
                    _wanted.Add(r);
            }

            if (_wanted.Count > 0)
                Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Forgets that <paramref name="key"/> was made (its texture was evicted; it may be requested again).</summary>
    public void Forget(string key)
    {
        lock (_gate)
            _done.Remove(key);
    }

    /// <summary>Takes a finished picture.</summary>
    public bool TryTake(out ThumbnailResult result)
    {
        lock (_gate)
        {
            if (_results.Count > 0)
            {
                result = _results.Dequeue();
                return true;
            }
        }

        result = null!;
        return false;
    }

    /// <summary>Stops the worker (the picture being made is finished first, at most a few seconds).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            _wanted.Clear();
            Monitor.PulseAll(_gate);
        }

        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            _log.Warn("Library", "The thumbnail worker did not stop in time.");
    }

    private void Run()
    {
        VulkanRenderer? renderer = null;
        FixtureThumbnailRenderer? thumbnails = null;
        try
        {
            while (true)
            {
                ThumbnailRequest request;
                lock (_gate)
                {
                    while (!_stop && _wanted.Count == 0)
                        Monitor.Wait(_gate);
                    if (_stop)
                        return;
                    request = _wanted[0];
                    _wanted.RemoveAt(0);
                    _current = request.Key;
                }

                ImageData? image = null;
                try
                {
                    image = request.Kind switch
                    {
                        ThumbnailKind.Image => ImageThumbnail(request),
                        _ => RenderFixture(request, ref renderer, ref thumbnails),
                    };
                }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    // One bad file never stops the others: the panel shows an icon for it.
                    _log.Warn("Library", $"No picture for '{request.Entry?.FileName ?? request.Path}': {e.Message}");
                }

                lock (_gate)
                {
                    _current = null;
                    _done.Add(request.Key);
                    _results.Enqueue(new ThumbnailResult(request.Key, image));
                }

                Thread.Sleep(Pause);
            }
        }
        finally
        {
            thumbnails?.Dispose();
            renderer?.Dispose();
        }
    }

    private ImageData? RenderFixture(ThumbnailRequest request, ref VulkanRenderer? renderer, ref FixtureThumbnailRenderer? thumbnails)
    {
        if (request.Entry is not { } entry || _error is not null)
            return null;
        if (renderer is null)
        {
            try
            {
                renderer = VulkanRenderer.Create(new VulkanContextOptions
                {
                    PreferredDeviceName = _deviceName,
                    EnableRayTracing = false,
                    CacheDirectory = _cacheDirectory,
                    ApplicationName = "Candela thumbnails",
                    Log = (level, message) =>
                    {
                        if (level >= VulkanLogLevel.Warning)
                            _log.Warn("Library", $"Thumbnail renderer: {message}");
                    },
                });
                thumbnails = new FixtureThumbnailRenderer(renderer, _library, _store);
                _log.Info("Library", $"Thumbnail renderer ready ({renderer.Info.DeviceName}).");
            }
            catch (Exception e) when (e is VulkanException or InvalidOperationException or DllNotFoundException)
            {
                _error = e.Message;
                _log.Warn("Library", $"Fixture pictures are not available: {e.Message}");
                return null;
            }
        }

        int size = Math.Clamp(request.Size, 16, ThumbnailStore.MaxSize);
        return request.Kind == ThumbnailKind.FixtureStill
            ? thumbnails!.Render(entry, size)
            : thumbnails!.RenderOrbitFrame(entry, size, size, request.Azimuth);
    }

    private static ImageData? ImageThumbnail(ThumbnailRequest request)
    {
        if (request.Path is not { } path || !File.Exists(path))
            return null;
        var info = new FileInfo(path);
        if (info.Length > 256L * 1024 * 1024)
            return null;
        var image = Candela.Assets.ImageImporter.Load(path);
        return image.Format == PixelFormat.Rgba8 ? ImageResize.Fit(image, Math.Clamp(request.Size, 16, 1024)) : null;
    }

    /// <summary>Cache key of a fixture's still thumbnail (stable across runs: file name, size, write time).</summary>
    public static string StillKey(FixtureLibraryEntry entry, int size) =>
        $"still|{entry.FilePath}|{entry.FileSize}|{entry.LastWriteTimeUtc.Ticks}|{size}";

    /// <summary>Cache key of an orbit frame.</summary>
    public static string OrbitKey(FixtureLibraryEntry entry, int size, int frame) =>
        $"orbit|{entry.FilePath}|{entry.LastWriteTimeUtc.Ticks}|{size}|{frame}";

    /// <summary>Cache key of an image thumbnail.</summary>
    public static string ImageKey(string path, int size) => $"image|{path}|{size}";
}

/// <summary>Downscales RGBA8 images for thumbnails (area average, aspect kept, centred on a transparent square).</summary>
internal static class ImageResize
{
    /// <summary>
    /// <paramref name="image"/> scaled to fit a <paramref name="size"/> × <paramref name="size"/> square (never enlarged),
    /// centred, with transparent borders.
    /// </summary>
    public static ImageData Fit(ImageData image, int size)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Format != PixelFormat.Rgba8 || image.Width <= 0 || image.Height <= 0)
            throw new ArgumentException("An RGBA8 image is needed.", nameof(image));
        size = Math.Max(1, size);
        float scale = Math.Min(1f, Math.Min(size / (float)image.Width, size / (float)image.Height));
        int w = Math.Max(1, (int)MathF.Round(image.Width * scale));
        int h = Math.Max(1, (int)MathF.Round(image.Height * scale));
        var output = new byte[size * size * 4];
        int ox = (size - w) / 2, oy = (size - h) / 2;
        var src = image.Pixels;
        for (int y = 0; y < h; y++)
        {
            int y0 = (int)((long)y * image.Height / h), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * image.Height / h));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)((long)x * image.Width / w), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * image.Width / w));
                long r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                // At most 4 × 4 samples per output pixel: plenty for a thumbnail, bounded for huge images.
                int stepY = Math.Max(1, (y1 - y0) / 4), stepX = Math.Max(1, (x1 - x0) / 4);
                for (int sy = y0; sy < y1; sy += stepY)
                {
                    for (int sx = x0; sx < x1; sx += stepX)
                    {
                        int i = (sy * image.Width + sx) * 4;
                        r += src[i];
                        g += src[i + 1];
                        b += src[i + 2];
                        a += src[i + 3];
                        n++;
                    }
                }

                int o = ((oy + y) * size + ox + x) * 4;
                output[o] = (byte)(r / n);
                output[o + 1] = (byte)(g / n);
                output[o + 2] = (byte)(b / n);
                output[o + 3] = (byte)(a / n);
            }
        }

        return new ImageData { Width = size, Height = size, Format = PixelFormat.Rgba8, Pixels = output };
    }
}
