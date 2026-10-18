using System.Diagnostics.CodeAnalysis;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>Shared state of one import: files, options, warnings and the images the materials end up referencing.</summary>
internal sealed class ImportContext
{
    private readonly HashSet<string> _warningSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageData?> _decoded = new(StringComparer.Ordinal);

    public ImportContext(ModelFileSet files, ImportOptions options, ModelFormat format)
    {
        Files = files;
        Options = options;
        Format = format;
    }

    public ModelFileSet Files { get; }
    public ImportOptions Options { get; }
    public ModelFormat Format { get; }
    public List<string> Warnings { get; } = new();
    public Dictionary<string, byte[]> Embedded { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ImageData> Generated { get; } = new(StringComparer.Ordinal);

    /// <summary>What <see cref="ObjSanitizer"/> removed from an OBJ import (null for other formats).</summary>
    public ObjSanitizer.Report? ObjReport { get; set; }

    /// <summary>Adds a warning once.</summary>
    public void Warn(string message)
    {
        if (_warningSet.Add(message)) Warnings.Add(message);
    }

    /// <summary>Texture key for a relative image path (prefix from the options).</summary>
    public string Key(string relativePath) => Options.TextureKeyPrefix + relativePath;

    /// <summary>
    /// Resolves an external texture reference to its relative key, checking that the file is an image format
    /// <see cref="ImageImporter"/> decodes. Missing or unsupported files return null and produce a warning.
    /// </summary>
    public string? ResolveExternalImage(string reference)
    {
        if (!Files.TryRead(reference, out var key, out var data))
        {
            Warn($"Texture '{reference}' was not found next to the model; the material is used without it.");
            return null;
        }
        if (ImageImporter.DetectFormat(data) is null)
        {
            Warn($"Texture '{key}' is in a format Candela cannot read; convert it to PNG or JPEG.");
            if (key != Files.MainFile) Files.Discard(key);
            return null;
        }
        return key;
    }

    /// <summary>Stores an encoded embedded image and returns its relative key (<c>embedded/…</c>).</summary>
    public string? AddEmbedded(string name, byte[] data)
    {
        if (ImageImporter.DetectFormat(data) is null)
        {
            Warn($"An embedded texture ('{name}') is in a format Candela cannot read; it is ignored.");
            return null;
        }
        var key = Unique("embedded/" + Sanitize(name), k => Embedded.ContainsKey(k));
        Embedded[key] = data;
        return key;
    }

    /// <summary>Stores a derived image and returns its relative key (<c>generated/…</c>).</summary>
    public string AddGenerated(string name, ImageData image)
    {
        var key = Unique("generated/" + Sanitize(name), k => Generated.ContainsKey(k));
        Generated[key] = image;
        _decoded[key] = image;
        return key;
    }

    /// <summary>Decodes an image addressed by relative key (file, embedded or generated); null when it cannot be decoded.</summary>
    public bool TryDecode(string relativeKey, [NotNullWhen(true)] out ImageData? image)
    {
        if (!_decoded.TryGetValue(relativeKey, out image))
        {
            byte[]? bytes = Embedded.TryGetValue(relativeKey, out var e) ? e
                : Files.TryRead(relativeKey, out _, out var f) ? f : null;
            image = null;
            if (bytes is not null)
            {
                if (ImageImporter.TryDecode(bytes, out var decoded, out var error)) image = decoded;
                else Warn($"Texture '{relativeKey}' could not be decoded: {error}");
            }
            _decoded[relativeKey] = image;
        }
        return image is not null;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Replace('\\', '/').Split('/').Last().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] < 32 || chars[i] is ':' or '*' or '?' or '"' or '<' or '>' or '|') chars[i] = '_';
        var s = new string(chars).Trim();
        return s.Length == 0 ? "image" : s;
    }

    private static string Unique(string key, Func<string, bool> exists)
    {
        if (!exists(key)) return key;
        var ext = Path.GetExtension(key);
        var stem = key[..^ext.Length];
        for (int i = 2; ; i++)
        {
            var k = $"{stem}-{i}{ext}";
            if (!exists(k)) return k;
        }
    }
}
