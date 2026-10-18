using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.Assets;
using Candela.Core.Geometry;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace Candela.Assets.Tests;

/// <summary>A temporary folder deleted when the test ends.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "candela-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    public string Write(string relative, byte[] data)
    {
        var full = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, data);
        return full;
    }

    public string Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Writers for small model and image files used by the tests (all content is generated, nothing downloaded).</summary>
internal static class TestFiles
{
    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A solid colour or checker RGBA8 image.</summary>
    public static ImageData Image(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var p = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int i = (y * w + x) * 4;
                p[i] = r;
                p[i + 1] = g;
                p[i + 2] = b;
                p[i + 3] = a;
            }
        return new ImageData { Width = w, Height = h, Format = PixelFormat.Rgba8, Pixels = p };
    }

    public static byte[] Png(int w, int h, byte r, byte g, byte b) => ImageImporter.EncodePng(Image(w, h, (_, _) => (r, g, b, 255)));

    /// <summary>
    /// Corners of an axis-aligned box; <paramref name="yUp"/> puts the height on Y (base at y = 0), otherwise on Z (base at
    /// z = 0). Width (X) 1 m, height 2 m, depth 0.5 m, all multiplied by <paramref name="scale"/>.
    /// </summary>
    public static Vector3[] BoxCorners(bool yUp, float scale = 1f)
    {
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            float x = (i & 1) == 0 ? -0.5f : 0.5f;
            float h = (i & 2) == 0 ? 0f : 2f;
            float d = (i & 4) == 0 ? -0.25f : 0.25f;
            c[i] = (yUp ? new Vector3(x, h, d) : new Vector3(x, -d, h)) * scale;
        }
        return c;
    }

    /// <summary>Outward counter-clockwise quads over <see cref="BoxCorners"/> (corner indices, either convention).</summary>
    public static int[][] BoxQuads() => s_boxQuads;

    private static readonly int[][] s_boxQuads = CreateBoxQuads();

    private static int[][] CreateBoxQuads()
    {
        // Corner index bits: 1 = +x, 2 = top, 4 = +depth. In Y-up "depth" is +Z; in Z-up it is −Y. (x, h, d) → (x, −d, h)
        // is a proper rotation, so the same corner order is counter-clockwise from outside in both conventions.
        var quads = new[]
        {
            new[] { 4, 5, 7, 6 }, // +depth face
            new[] { 1, 0, 2, 3 }, // −depth face
            new[] { 5, 1, 3, 7 }, // +x
            new[] { 0, 4, 6, 2 }, // −x
            new[] { 6, 7, 3, 2 }, // top
            new[] { 0, 1, 5, 4 }, // bottom
        };
        return quads;
    }

    public static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> BoxTriangles(bool yUp, float scale = 1f)
    {
        var c = BoxCorners(yUp, scale);
        foreach (var q in BoxQuads())
        {
            yield return (c[q[0]], c[q[1]], c[q[2]]);
            yield return (c[q[0]], c[q[2]], c[q[3]]);
        }
    }

    /// <summary>OBJ (Y-up box) with texture coordinates and a material library reference.</summary>
    public static string Obj(string mtlFile, string material)
    {
        var sb = new StringBuilder("# test box\nmtllib " + mtlFile + "\no Box\n");
        foreach (var v in BoxCorners(yUp: true)) sb.Append($"v {F(v.X)} {F(v.Y)} {F(v.Z)}\n");
        sb.Append("vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nusemtl " + material + "\n");
        foreach (var q in BoxQuads()) sb.Append($"f {q[0] + 1}/1 {q[1] + 1}/2 {q[2] + 1}/3 {q[3] + 1}/4\n");
        return sb.ToString();
    }

    public static string AsciiStl(float scale = 1f)
    {
        var sb = new StringBuilder("solid box\n");
        foreach (var (a, b, c) in BoxTriangles(yUp: false, scale))
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            sb.Append($"facet normal {F(n.X)} {F(n.Y)} {F(n.Z)}\n outer loop\n");
            foreach (var v in new[] { a, b, c }) sb.Append($"  vertex {F(v.X)} {F(v.Y)} {F(v.Z)}\n");
            sb.Append(" endloop\nendfacet\n");
        }
        return sb.Append("endsolid box\n").ToString();
    }

    public static byte[] BinaryStl(IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        var list = triangles.ToList();
        using var ms = new MemoryStream(84 + list.Count * 50);
        using var w = new BinaryWriter(ms);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("binary test box").CopyTo(header, 0);
        w.Write(header);
        w.Write((uint)list.Count);
        foreach (var (a, b, c) in list)
        {
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
            foreach (var v in new[] { n, a, b, c })
            {
                w.Write(v.X);
                w.Write(v.Y);
                w.Write(v.Z);
            }
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A flat grid of 2 × cells² triangles in the XY plane (for performance tests), Z-up, 10 × 10 m.</summary>
    public static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Grid(int cells)
    {
        float s = 10f / cells;
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                var p00 = new Vector3(x * s, y * s, MathF.Sin(x * 0.1f) * 0.1f);
                var p10 = new Vector3((x + 1) * s, y * s, MathF.Sin((x + 1) * 0.1f) * 0.1f);
                var p01 = new Vector3(x * s, (y + 1) * s, MathF.Sin(x * 0.1f) * 0.1f);
                var p11 = new Vector3((x + 1) * s, (y + 1) * s, MathF.Sin((x + 1) * 0.1f) * 0.1f);
                yield return (p00, p10, p11);
                yield return (p00, p11, p01);
            }
    }

    /// <summary>COLLADA box in centimetres, Z-up (<c>&lt;unit meter="0.01"&gt;</c>), with a red Phong material.</summary>
    public static string Collada(float meter = 0.01f, string upAxis = "Z_UP")
    {
        bool yUp = upAxis == "Y_UP";
        float scale = 1f / meter;
        var corners = BoxCorners(yUp, scale);
        var tris = BoxQuads().SelectMany(q => new[] { q[0], q[1], q[2], q[0], q[2], q[3] }).ToArray();
        string positions = string.Join(" ", corners.SelectMany(c => new[] { F(c.X), F(c.Y), F(c.Z) }));
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
              <asset><unit name="unit" meter="{F(meter)}"/><up_axis>{upAxis}</up_axis></asset>
              <library_effects>
                <effect id="red-fx"><profile_COMMON><technique sid="common"><phong>
                  <diffuse><color>0.8 0.1 0.1 1</color></diffuse>
                  <specular><color>0.5 0.5 0.5 1</color></specular>
                  <shininess><float>50</float></shininess>
                </phong></technique></profile_COMMON></effect>
              </library_effects>
              <library_materials><material id="red" name="red"><instance_effect url="#red-fx"/></material></library_materials>
              <library_geometries>
                <geometry id="box-mesh" name="box">
                  <mesh>
                    <source id="box-pos">
                      <float_array id="box-pos-array" count="24">{positions}</float_array>
                      <technique_common><accessor source="#box-pos-array" count="8" stride="3"><param name="X" type="float"/><param name="Y" type="float"/><param name="Z" type="float"/></accessor></technique_common>
                    </source>
                    <vertices id="box-vtx"><input semantic="POSITION" source="#box-pos"/></vertices>
                    <triangles count="12" material="mat"><input semantic="VERTEX" source="#box-vtx" offset="0"/><p>{string.Join(" ", tris)}</p></triangles>
                  </mesh>
                </geometry>
              </library_geometries>
              <library_visual_scenes>
                <visual_scene id="Scene"><node id="box" name="box"><instance_geometry url="#box-mesh"><bind_material><technique_common><instance_material symbol="mat" target="#red"/></technique_common></bind_material></instance_geometry></node></visual_scene>
              </library_visual_scenes>
              <scene><instance_visual_scene url="#Scene"/></scene>
            </COLLADA>
            """;
    }

    public static string AsciiPly()
    {
        var c = BoxCorners(yUp: false);
        var sb = new StringBuilder("ply\nformat ascii 1.0\nelement vertex 8\nproperty float x\nproperty float y\nproperty float z\nelement face 6\nproperty list uchar int vertex_indices\nend_header\n");
        foreach (var v in c) sb.Append($"{F(v.X)} {F(v.Y)} {F(v.Z)}\n");
        foreach (var q in BoxQuads()) sb.Append($"4 {q[0]} {q[1]} {q[2]} {q[3]}\n");
        return sb.ToString();
    }

    /// <summary>Minimal binary 3DS: one mesh object (Z-up box) without materials.</summary>
    public static byte[] ThreeDs()
    {
        static byte[] Chunk(ushort id, params byte[][] body)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(id);
            w.Write(6 + body.Sum(b => b.Length));
            foreach (var b in body) w.Write(b);
            w.Flush();
            return ms.ToArray();
        }
        var corners = BoxCorners(yUp: false);
        var vms = new MemoryStream();
        var vw = new BinaryWriter(vms);
        vw.Write((ushort)corners.Length);
        foreach (var v in corners)
        {
            vw.Write(v.X);
            vw.Write(v.Y);
            vw.Write(v.Z);
        }
        var tris = BoxQuads().SelectMany(q => new[] { (q[0], q[1], q[2]), (q[0], q[2], q[3]) }).ToArray();
        var fms = new MemoryStream();
        var fw = new BinaryWriter(fms);
        fw.Write((ushort)tris.Length);
        foreach (var (a, b, c) in tris)
        {
            fw.Write((ushort)a);
            fw.Write((ushort)b);
            fw.Write((ushort)c);
            fw.Write((ushort)7);
        }
        var trimesh = Chunk(0x4100, Chunk(0x4110, vms.ToArray()), Chunk(0x4120, fms.ToArray()));
        var obj = Chunk(0x4000, Encoding.ASCII.GetBytes("box\0"), trimesh);
        var edit = Chunk(0x3D3D, Chunk(0x3D3E, BitConverter.GetBytes(3u)), obj);
        return Chunk(0x4D4D, Chunk(0x0002, BitConverter.GetBytes(3u)), edit);
    }

    /// <summary>
    /// ASCII FBX 7.4 box. <paramref name="zUp"/>: 3ds Max axis system (Z up, front −Y); otherwise Maya (Y up, front +Z).
    /// Coordinates are written in units of <paramref name="unitScaleFactorCm"/> centimetres.
    /// </summary>
    public static string AsciiFbx(bool zUp, double unitScaleFactorCm)
    {
        float toUnits = (float)(100.0 / unitScaleFactorCm);
        var corners = BoxCorners(yUp: !zUp, toUnits);
        var poly = BoxQuads().SelectMany(q => new[] { q[0], q[1], q[2], -(q[3] + 1) });
        string axes = zUp
            ? """
                P: "UpAxis", "int", "Integer", "",2
                P: "UpAxisSign", "int", "Integer", "",1
                P: "FrontAxis", "int", "Integer", "",1
                P: "FrontAxisSign", "int", "Integer", "",-1
                P: "CoordAxis", "int", "Integer", "",0
                P: "CoordAxisSign", "int", "Integer", "",1
            """
            : """
                P: "UpAxis", "int", "Integer", "",1
                P: "UpAxisSign", "int", "Integer", "",1
                P: "FrontAxis", "int", "Integer", "",2
                P: "FrontAxisSign", "int", "Integer", "",1
                P: "CoordAxis", "int", "Integer", "",0
                P: "CoordAxisSign", "int", "Integer", "",1
            """;
        return $$"""
            ; FBX 7.4.0 project file
            FBXHeaderExtension:  {
                FBXHeaderVersion: 1003
                FBXVersion: 7400
            }
            GlobalSettings:  {
                Version: 1000
                Properties70:  {
            {{axes}}
                    P: "UnitScaleFactor", "double", "Number", "",{{unitScaleFactorCm.ToString("R", CultureInfo.InvariantCulture)}}
                }
            }
            Objects:  {
                Geometry: 1000, "Geometry::box", "Mesh" {
                    Vertices: *24 {
                        a: {{string.Join(",", corners.SelectMany(c => new[] { F(c.X), F(c.Y), F(c.Z) }))}}
                    }
                    PolygonVertexIndex: *24 {
                        a: {{string.Join(",", poly)}}
                    }
                    GeometryVersion: 124
                    LayerElementMaterial: 0 {
                        Version: 101
                        Name: ""
                        MappingInformationType: "AllSame"
                        ReferenceInformationType: "IndexToDirect"
                        Materials: *1 {
                            a: 0
                        }
                    }
                    Layer: 0 {
                        Version: 100
                        LayerElement:  {
                            Type: "LayerElementMaterial"
                            TypedIndex: 0
                        }
                    }
                }
                Model: 2000, "Model::box", "Mesh" {
                    Version: 232
                    Properties70:  {
                    }
                }
                Material: 3000, "Material::Chrome", "" {
                    Version: 102
                    ShadingModel: "phong"
                    Properties70:  {
                        P: "DiffuseColor", "Color", "", "A",0.05,0.05,0.05
                        P: "SpecularColor", "Color", "", "A",0.9,0.9,0.9
                        P: "Shininess", "double", "Number", "",200
                        P: "ShininessExponent", "double", "Number", "",200
                    }
                }
            }
            Connections:  {
                C: "OO",2000,0
                C: "OO",1000,2000
                C: "OO",3000,2000
            }
            """;
    }

    /// <summary>A Y-up box GLB built with SharpGLTF (positions, normals, UVs, PBR material with an embedded PNG).</summary>
    public static byte[] Glb(bool withTexture = true) => GltfModel(withTexture).WriteGLB().ToArray();

    /// <summary>The model behind <see cref="Glb"/>; save it with <c>SaveGLTF</c> for the .gltf + .bin + .png flavour.</summary>
    public static ModelRoot GltfModel(bool withTexture = true)
    {
        var model = ModelRoot.CreateModel();
        var corners = BoxCorners(yUp: true);
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var uv = new List<Vector2>();
        var idx = new List<uint>();
        foreach (var q in BoxQuads())
        {
            var n = Vector3.Normalize(Vector3.Cross(corners[q[1]] - corners[q[0]], corners[q[2]] - corners[q[0]]));
            uint b = (uint)pos.Count;
            for (int k = 0; k < 4; k++)
            {
                pos.Add(corners[q[k]]);
                nrm.Add(n);
                uv.Add(new Vector2(k is 1 or 2 ? 1 : 0, k >= 2 ? 0 : 1));
            }
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        Accessor Vertex<T>(string name, List<T> data, DimensionType dim) where T : unmanaged
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data)).ToArray();
            var view = model.UseBufferView(bytes, 0, null, 0, BufferMode.ARRAY_BUFFER);
            var acc = model.CreateAccessor(name);
            acc.SetVertexData(view, 0, data.Count, new AttributeFormat(dim, EncodingType.FLOAT, false));
            return acc;
        }

        var mesh = model.CreateMesh("box");
        var prim = mesh.CreatePrimitive();
        prim.SetVertexAccessor("POSITION", Vertex("pos", pos, DimensionType.VEC3));
        prim.SetVertexAccessor("NORMAL", Vertex("nrm", nrm, DimensionType.VEC3));
        prim.SetVertexAccessor("TEXCOORD_0", Vertex("uv", uv, DimensionType.VEC2));
        var idxBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(idx.ToArray().AsSpan()).ToArray();
        var idxView = model.UseBufferView(idxBytes, 0, null, 0, BufferMode.ELEMENT_ARRAY_BUFFER);
        var idxAcc = model.CreateAccessor("idx");
        idxAcc.SetIndexData(idxView, 0, idx.Count, IndexEncodingType.UNSIGNED_INT);
        prim.SetIndexAccessor(idxAcc);

        var material = model.CreateMaterial("Painted");
        material.InitializePBRMetallicRoughness();
        var baseColor = material.FindChannel("BaseColor")!.Value;
        baseColor.Color = new Vector4(0.9f, 0.5f, 0.2f, 1f);
        if (withTexture)
        {
            var image = model.UseImage(new MemoryImage(Png(4, 4, 0, 200, 0)));
            image.Name = "green";
            baseColor.SetTexture(0, image, null!, TextureWrapMode.REPEAT, TextureWrapMode.REPEAT,
                TextureMipMapFilter.LINEAR_MIPMAP_LINEAR, TextureInterpolationFilter.LINEAR);
        }
        var mr = material.FindChannel("MetallicRoughness")!.Value;
        mr.SetFactor("MetallicFactor", 0f);
        mr.SetFactor("RoughnessFactor", 0.4f);
        prim.Material = material;

        var scene = model.UseScene("scene");
        scene.CreateNode("box").Mesh = mesh;
        return model;
    }

    /// <summary>Header of a SketchUp file (0xFF 0xFE 0xFF, length, "SketchUp Model" in UTF-16LE) plus filler.</summary>
    public static byte[] SketchUpHeader()
    {
        var text = Encoding.Unicode.GetBytes("SketchUp Model");
        return new byte[] { 0xFF, 0xFE, 0xFF, 0x0E }.Concat(text).Concat(new byte[64]).ToArray();
    }
}

/// <summary>Geometry checks used across the tests.</summary>
internal static class Geo
{
    /// <summary>Signed volume of a closed model (parts transformed): positive when faces point outwards.</summary>
    public static double SignedVolume(ModelData model)
    {
        double v = 0;
        foreach (var part in model.Parts)
        {
            var m = part.Mesh;
            for (int i = 0; i + 2 < m.Indices.Length; i += 3)
            {
                var a = Vector3.Transform(m.Positions[m.Indices[i]], part.Transform);
                var b = Vector3.Transform(m.Positions[m.Indices[i + 1]], part.Transform);
                var c = Vector3.Transform(m.Positions[m.Indices[i + 2]], part.Transform);
                v += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
        }
        return v;
    }

    /// <summary>True when every vertex normal agrees with the faces that use it (winding and normals consistent).</summary>
    public static bool NormalsAgreeWithWinding(MeshData m)
    {
        for (int i = 0; i + 2 < m.Indices.Length; i += 3)
        {
            uint a = m.Indices[i], b = m.Indices[i + 1], c = m.Indices[i + 2];
            var fn = Vector3.Cross(m.Positions[b] - m.Positions[a], m.Positions[c] - m.Positions[a]);
            if (fn.LengthSquared() < 1e-20f) continue;
            foreach (var k in new[] { a, b, c })
                if (Vector3.Dot(fn, m.Normals[k]) <= 0) return false;
        }
        return true;
    }

    public static void AssertSize(Vector3 expected, Vector3 actual, float tolerance = 1e-3f)
    {
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected size {expected}, got {actual}");
    }

    public static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-3f)
    {
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");
    }
}
