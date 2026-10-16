using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Candela.Core.Math;
using Candela.Core.SceneGraph;

namespace Candela.Core.Serialization;

/// <summary>
/// The one JSON configuration used for project files, presets and settings: camelCase, enums as strings, vectors as
/// arrays, polymorphic components with a "$type" discriminator, tolerant of unknown properties (forward compatible).
/// </summary>
public static class CandelaJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);
    public static JsonSerializerOptions CompactOptions { get; } = CreateOptions(indented: false);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new Vector2Converter());
        o.Converters.Add(new Vector3Converter());
        o.Converters.Add(new Vector4Converter());
        o.Converters.Add(new QuaternionConverter());
        o.Converters.Add(new Matrix4x4Converter());
        o.Converters.Add(new TransformConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : CompactOptions);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deep copy through JSON. For components the copy keeps its concrete type.</summary>
    public static T DeepClone<T>(T value) where T : class
    {
        if (value is Component c)
            return (T)(object)JsonSerializer.Deserialize<Component>(JsonSerializer.Serialize(c, CompactOptions), CompactOptions)!;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, value.GetType(), CompactOptions), CompactOptions)!;
    }

    /// <summary>Component → JSON including its "$type".</summary>
    public static string SerializeComponent(Component component) => JsonSerializer.Serialize(component, CompactOptions);

    /// <summary>
    /// Overwrites every public settable property of <paramref name="target"/> with the values stored in
    /// <paramref name="json"/> (same concrete type). Used by undo of in-place component edits.
    /// </summary>
    public static void PopulateComponent(Component target, string json)
    {
        var source = JsonSerializer.Deserialize<Component>(json, CompactOptions)
                     ?? throw new InvalidDataException("Component JSON is empty.");
        if (source.GetType() != target.GetType())
            throw new InvalidDataException($"Cannot populate {target.GetType().Name} from {source.GetType().Name}.");
        foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.Name == nameof(Component.Owner) || !p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            p.SetValue(target, p.GetValue(source));
        }
    }

    private sealed class Vector2Converter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var a = ReadFloats(ref r, 2);
            return new Vector2(a[0], a[1]);
        }

        public override void Write(Utf8JsonWriter w, Vector2 v, JsonSerializerOptions o) => WriteFloats(w, v.X, v.Y);
    }

    private sealed class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var a = ReadFloats(ref r, 3);
            return new Vector3(a[0], a[1], a[2]);
        }

        public override void Write(Utf8JsonWriter w, Vector3 v, JsonSerializerOptions o) => WriteFloats(w, v.X, v.Y, v.Z);
    }

    private sealed class Vector4Converter : JsonConverter<Vector4>
    {
        public override Vector4 Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var a = ReadFloats(ref r, 4);
            return new Vector4(a[0], a[1], a[2], a[3]);
        }

        public override void Write(Utf8JsonWriter w, Vector4 v, JsonSerializerOptions o) => WriteFloats(w, v.X, v.Y, v.Z, v.W);
    }

    private sealed class QuaternionConverter : JsonConverter<Quaternion>
    {
        public override Quaternion Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var a = ReadFloats(ref r, 4);
            return new Quaternion(a[0], a[1], a[2], a[3]);
        }

        public override void Write(Utf8JsonWriter w, Quaternion v, JsonSerializerOptions o) => WriteFloats(w, v.X, v.Y, v.Z, v.W);
    }

    /// <summary>Row-major 16 numbers (System.Numerics M11..M44).</summary>
    private sealed class Matrix4x4Converter : JsonConverter<Matrix4x4>
    {
        public override Matrix4x4 Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var a = ReadFloats(ref r, 16);
            return new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
        }

        public override void Write(Utf8JsonWriter w, Matrix4x4 m, JsonSerializerOptions o) =>
            WriteFloats(w, m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    }

    /// <summary>{ "position": [x,y,z], "rotation": [x,y,z,w], "scale": [x,y,z] }; missing parts default to identity.</summary>
    private sealed class TransformConverter : JsonConverter<Transform>
    {
        public override Transform Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("Transform must be an object.");
            var tr = Transform.Identity;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                var name = r.GetString();
                r.Read();
                switch (name)
                {
                    case "position": tr.Position = JsonSerializer.Deserialize<Vector3>(ref r, o); break;
                    case "rotation": tr.Rotation = JsonSerializer.Deserialize<Quaternion>(ref r, o); break;
                    case "scale": tr.Scale = JsonSerializer.Deserialize<Vector3>(ref r, o); break;
                    default: r.Skip(); break;
                }
            }
            return tr;
        }

        public override void Write(Utf8JsonWriter w, Transform v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WritePropertyName("position");
            JsonSerializer.Serialize(w, v.Position, o);
            w.WritePropertyName("rotation");
            JsonSerializer.Serialize(w, v.Rotation, o);
            w.WritePropertyName("scale");
            JsonSerializer.Serialize(w, v.Scale, o);
            w.WriteEndObject();
        }
    }

    private static float[] ReadFloats(ref Utf8JsonReader r, int count)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException($"Expected an array of {count} numbers.");
        var a = new float[count];
        int i = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (i >= count) throw new JsonException($"Too many numbers (expected {count}).");
            a[i++] = r.TokenType == JsonTokenType.String ? float.Parse(r.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : r.GetSingle();
        }
        if (i != count) throw new JsonException($"Expected {count} numbers, got {i}.");
        return a;
    }

    private static void WriteFloats(Utf8JsonWriter w, params float[] values)
    {
        w.WriteStartArray();
        foreach (var f in values)
        {
            if (float.IsFinite(f)) w.WriteNumberValue(f);
            else w.WriteStringValue(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        w.WriteEndArray();
    }
}
