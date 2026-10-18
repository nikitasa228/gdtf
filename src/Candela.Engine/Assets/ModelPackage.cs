using System.Numerics;
using System.Text;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Core.Serialization;

namespace Candela.Engine;

/// <summary>
/// Candela's own binary serialisation of an imported model (<see cref="ImportedModel"/>: meshes, part transforms,
/// materials, the files that were read, embedded and generated images, warnings). The import worker writes it, the
/// application reads it back, and the engine keeps it as the decoded-model cache, so a model decoded once by the
/// native importer never has to go through it again.
/// </summary>
/// <remarks>
/// Layout (little-endian): magic <c>CMDL</c>, format version, header strings and numbers, the file and image tables,
/// then the model. Reading is defensive because the writer processed untrusted input: every count and length is
/// checked against the bytes that remain, so a damaged or hostile package fails with <see cref="InvalidDataException"/>
/// instead of exhausting memory.
/// </remarks>
public static class ModelPackage
{
    /// <summary>File extension of a package.</summary>
    public const string Extension = ".cmodel";

    /// <summary>Current format version.</summary>
    public const int FormatVersion = 1;

    private static readonly byte[] Magic = "CMDL"u8.ToArray();
    private const int MaxStringBytes = 1 << 20;

    /// <summary>Writes <paramref name="model"/> to <paramref name="output"/>.</summary>
    public static void Write(ImportedModel model, Stream output)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(output);
        using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(model.Format.ToString());
        w.Write(model.MainFile);
        w.Write(model.SourceUpAxis.ToString());
        w.Write(model.UnitScale);
        w.Write(model.UnitsKnown);
        WriteStrings(w, model.Warnings);
        WriteStrings(w, model.MissingFiles);
        WriteBlobs(w, model.Files);
        WriteBlobs(w, model.EmbeddedImages);
        w.Write(model.GeneratedImages.Count);
        foreach (var (key, image) in model.GeneratedImages.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.Write(key);
            WriteImage(w, image);
        }
        WriteModel(w, model.Model);
    }

    /// <summary>Writes <paramref name="model"/> to a file atomically (temporary file + rename).</summary>
    public static void WriteFile(ImportedModel model, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(model, fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Reads a package. Throws <see cref="InvalidDataException"/> for anything that is not a valid package.</summary>
    public static ImportedModel Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            using var r = new Reader(input);
            var magic = r.Bytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a Candela model package.");
            int version = r.Int();
            if (version != FormatVersion) throw new InvalidDataException($"Unsupported model package version {version}.");
            var format = Enum.TryParse<ModelFormat>(r.String(), out var f) ? f : ModelFormat.Unknown;
            string main = r.String();
            var up = Enum.TryParse<SourceUpAxis>(r.String(), out var u) ? u : SourceUpAxis.Auto;
            float unitScale = r.Float();
            bool unitsKnown = r.Bool();
            var warnings = r.Strings();
            var missing = r.Strings();
            var files = r.Blobs();
            var embedded = r.Blobs();
            int generatedCount = r.Count(8);
            var generated = new Dictionary<string, ImageData>(StringComparer.Ordinal);
            for (int i = 0; i < generatedCount; i++)
            {
                string key = r.String();
                generated[key] = r.Image();
            }
            var model = r.Model();
            if (!files.ContainsKey(main)) throw new InvalidDataException("The package does not contain its main file.");
            return new ImportedModel
            {
                Model = model,
                Format = format,
                MainFile = main,
                Files = files,
                EmbeddedImages = embedded,
                GeneratedImages = generated,
                MissingFiles = missing,
                Warnings = warnings,
                SourceUpAxis = up,
                UnitScale = float.IsFinite(unitScale) && unitScale > 0f ? unitScale : 1f,
                UnitsKnown = unitsKnown,
            };
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The model package is truncated.", e);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException($"The model package is damaged: {e.Message}", e);
        }
    }

    /// <summary>Reads a package file.</summary>
    public static ImportedModel ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(fs);
    }

    private static void WriteStrings(BinaryWriter w, IReadOnlyList<string> values)
    {
        w.Write(values.Count);
        foreach (var v in values) w.Write(v ?? "");
    }

    private static void WriteBlobs(BinaryWriter w, IReadOnlyDictionary<string, byte[]> blobs)
    {
        w.Write(blobs.Count);
        foreach (var (key, bytes) in blobs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.Write(key);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }

    private static void WriteImage(BinaryWriter w, ImageData image)
    {
        w.Write(image.Width);
        w.Write(image.Height);
        w.Write((int)image.Format);
        w.Write(image.Pixels.Length);
        w.Write(image.Pixels);
    }

    private static void WriteModel(BinaryWriter w, ModelData model)
    {
        w.Write(model.Name ?? "");
        w.Write(model.Materials.Count);
        foreach (var m in model.Materials) w.Write(CandelaJson.Serialize(m, indented: false));
        w.Write(model.Parts.Count);
        foreach (var part in model.Parts)
        {
            w.Write(part.Name ?? "");
            var t = part.Transform;
            foreach (float f in new[] { t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24, t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44 })
                w.Write(f);
            WriteMesh(w, part.Mesh);
        }
    }

    private static void WriteMesh(BinaryWriter w, MeshData mesh)
    {
        w.Write(mesh.Name ?? "");
        w.Write(mesh.Positions.Length);
        foreach (var p in mesh.Positions) WriteVector(w, p);
        bool normals = mesh.Normals.Length == mesh.Positions.Length;
        w.Write(normals);
        if (normals) foreach (var n in mesh.Normals) WriteVector(w, n);
        bool uv = mesh.TexCoords is { } tc && tc.Length == mesh.Positions.Length;
        w.Write(uv);
        if (uv)
        {
            foreach (var t in mesh.TexCoords!)
            {
                w.Write(t.X);
                w.Write(t.Y);
            }
        }
        bool tangents = mesh.Tangents is { } tg && tg.Length == mesh.Positions.Length;
        w.Write(tangents);
        if (tangents)
        {
            foreach (var t in mesh.Tangents!)
            {
                w.Write(t.X);
                w.Write(t.Y);
                w.Write(t.Z);
                w.Write(t.W);
            }
        }
        w.Write(mesh.Indices.Length);
        foreach (uint i in mesh.Indices) w.Write(i);
        w.Write(mesh.SubMeshes.Count);
        foreach (var s in mesh.SubMeshes)
        {
            w.Write(s.IndexStart);
            w.Write(s.IndexCount);
            w.Write(s.MaterialSlot);
        }
    }

    private static void WriteVector(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    /// <summary>Bounds-checked reader: every count is validated against the bytes left in the stream.</summary>
    private sealed class Reader : IDisposable
    {
        private readonly BinaryReader _r;
        private readonly Stream _s;

        public Reader(Stream s)
        {
            _s = s;
            _r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        }

        private long Remaining => _s.CanSeek ? _s.Length - _s.Position : long.MaxValue;

        public void Dispose() => _r.Dispose();

        public int Int() => _r.ReadInt32();

        public float Float() => _r.ReadSingle();

        public bool Bool() => _r.ReadBoolean();

        public byte[] Bytes(int n)
        {
            if (n < 0 || n > Remaining) throw new InvalidDataException("A length in the model package is out of range.");
            var b = _r.ReadBytes(n);
            if (b.Length != n) throw new EndOfStreamException();
            return b;
        }

        /// <summary>A count whose elements take at least <paramref name="minBytesEach"/> bytes each.</summary>
        public int Count(long minBytesEach)
        {
            int n = _r.ReadInt32();
            if (n < 0 || (long)n * minBytesEach > Remaining) throw new InvalidDataException("A count in the model package is out of range.");
            return n;
        }

        public string String()
        {
            // BinaryReader.ReadString reads a 7-bit encoded length first; check it before allocating.
            long start = _s.CanSeek ? _s.Position : 0;
            int length = Read7BitLength();
            if (length > MaxStringBytes || length > Remaining) throw new InvalidDataException("A string in the model package is too long.");
            if (_s.CanSeek) _s.Position = start;
            return _r.ReadString();
        }

        private int Read7BitLength()
        {
            int result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = _r.ReadByte();
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new InvalidDataException("Invalid string length in the model package.");
        }

        public IReadOnlyList<string> Strings()
        {
            int n = Count(1);
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(String());
            return list;
        }

        public IReadOnlyDictionary<string, byte[]> Blobs()
        {
            int n = Count(5);
            var d = new Dictionary<string, byte[]>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                string key = String();
                d[key] = Bytes(Int());
            }
            return d;
        }

        public ImageData Image()
        {
            int width = Int(), height = Int(), format = Int();
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || format is < 0 or > 1)
                throw new InvalidDataException("An image in the model package has an invalid size or format.");
            var pixels = Bytes(Int());
            long expected = (long)width * height * (format == (int)PixelFormat.RgbaFloat ? 16 : 4);
            if (pixels.Length != expected) throw new InvalidDataException("An image in the model package has the wrong pixel count.");
            return new ImageData { Width = width, Height = height, Format = (PixelFormat)format, Pixels = pixels };
        }

        public ModelData Model()
        {
            var model = new ModelData { Name = String() };
            int materials = Count(1);
            for (int i = 0; i < materials; i++)
                model.Materials.Add(CandelaJson.Deserialize<MaterialData>(String()) ?? new MaterialData());
            int parts = Count(64 + 2);
            for (int i = 0; i < parts; i++)
            {
                string name = String();
                var m = new float[16];
                for (int k = 0; k < 16; k++) m[k] = Float();
                var transform = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                model.Parts.Add(new ModelPart { Name = name, Transform = transform, Mesh = Mesh() });
            }
            return model;
        }

        private MeshData Mesh()
        {
            var mesh = new MeshData { Name = String() };
            int n = Count(12);
            var positions = new Vector3[n];
            for (int i = 0; i < n; i++) positions[i] = Vector();
            mesh.Positions = positions;
            if (Bool())
            {
                if ((long)n * 12 > Remaining) throw new InvalidDataException("Truncated normals.");
                var normals = new Vector3[n];
                for (int i = 0; i < n; i++) normals[i] = Vector();
                mesh.Normals = normals;
            }
            else
            {
                mesh.ComputeNormals();
            }
            if (Bool())
            {
                if ((long)n * 8 > Remaining) throw new InvalidDataException("Truncated texture coordinates.");
                var uv = new Vector2[n];
                for (int i = 0; i < n; i++) uv[i] = new Vector2(Float(), Float());
                mesh.TexCoords = uv;
            }
            if (Bool())
            {
                if ((long)n * 16 > Remaining) throw new InvalidDataException("Truncated tangents.");
                var tangents = new Vector4[n];
                for (int i = 0; i < n; i++) tangents[i] = new Vector4(Float(), Float(), Float(), Float());
                mesh.Tangents = tangents;
            }
            int indexCount = Count(4);
            var indices = new uint[indexCount];
            for (int i = 0; i < indexCount; i++) indices[i] = _r.ReadUInt32();
            mesh.Indices = indices;
            int subMeshes = Count(12);
            for (int i = 0; i < subMeshes; i++) mesh.SubMeshes.Add(new SubMesh(Int(), Int(), Int()));
            try
            {
                mesh.Validate();
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"A mesh in the model package is invalid: {e.Message}", e);
            }
            foreach (var s in mesh.SubMeshes)
                if (s.IndexStart < 0 || s.IndexCount < 0 || (long)s.IndexStart + s.IndexCount > indices.Length || s.MaterialSlot < 0)
                    throw new InvalidDataException("A sub-mesh in the model package is out of range.");
            return mesh;
        }

        private Vector3 Vector() => new(Float(), Float(), Float());
    }
}
