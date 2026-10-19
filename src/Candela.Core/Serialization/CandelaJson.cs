using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Candela.Core.Math;
using Candela.Core.SceneGraph;

namespace Candela.Core.Serialization;

/// <summary>
/// The one JSON configuration used for project files, presets and settings: camelCase, enums as strings, vectors as
/// arrays (non-finite numbers as the strings "NaN", "Infinity", "-Infinity"), polymorphic components with a "$type"
/// discriminator, tolerant of unknown properties (forward compatible; Core's data classes keep them in their
/// <c>UnknownProperties</c> so a file written by a newer version survives being saved again).
/// Null values are omitted, except for a property whose default is not null (e.g.
/// <c>ConsoleComponent.ProtocolId</c>, default "artnet"): such a null is written as <c>null</c>, because an omitted
/// value reads back as the default. Writing with these options and reading back is therefore exact.
/// </summary>
public static class CandelaJson
{
    /// <summary>
    /// Deepest nesting of JSON objects and arrays the project formats read and write (System.Text.Json's default is 64).
    /// Scene files are flat (entities are listed, not nested), so the limit only bounds component data and properties of
    /// newer versions; parsing is iterative, so the limit guards against hostile input, not the stack.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>Indented options for files (project parts, presets, settings).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);

    /// <summary>Compact options for in-memory JSON.</summary>
    public static JsonSerializerOptions CompactOptions { get; } = CreateOptions(indented: false);

    /// <summary>
    /// Compact options that write every null (also those whose default is null). Copies and undo snapshots use them, so
    /// they are exact even for types whose default values cannot be determined (no parameterless constructor).
    /// </summary>
    internal static JsonSerializerOptions CopyOptions { get; } = CreateCopyOptions();

    private static JsonSerializerOptions CreateCopyOptions()
    {
        var o = new JsonSerializerOptions(CompactOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

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
            MaxDepth = MaxDepth,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WriteNullsThatDifferFromDefaults } },
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

    /// <summary>
    /// Contract modifier: a nullable property whose value on a freshly constructed object is not null is always written,
    /// so a null stays null through a write and a read (<see cref="JsonIgnoreCondition.WhenWritingNull"/> would omit it and
    /// the reader would restore the default). Types without a usable parameterless constructor keep the plain behaviour.
    /// </summary>
    private static void WriteNullsThatDifferFromDefaults(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.CreateObject is null || info.Type.IsAbstract) return;
        object sample;
        try
        {
            sample = info.CreateObject();
        }
        catch (Exception)
        {
            return; // a constructor that fails cannot tell the defaults; nulls are omitted as before
        }
        foreach (var property in info.Properties)
        {
            if (property.Get is null || property.IsExtensionData) continue;
            var type = property.PropertyType;
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null) continue;
            object? defaultValue;
            try
            {
                defaultValue = property.Get(sample);
            }
            catch (Exception)
            {
                continue;
            }
            if (defaultValue is not null) property.ShouldSerialize = static (_, _) => true;
        }
    }

    /// <summary>Serialises <paramref name="value"/> with <see cref="Options"/> (or <see cref="CompactOptions"/>).</summary>
    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : CompactOptions);

    /// <summary>Deserialises with <see cref="Options"/>.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Exact deep copy through JSON (nulls included, see <see cref="CopyOptions"/>). For components the copy keeps its
    /// concrete type and is detached.
    /// </summary>
    public static T DeepClone<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is Component c)
        {
            return (T)(object)(JsonSerializer.Deserialize<Component>(JsonSerializer.SerializeToUtf8Bytes(c, CopyOptions), CopyOptions)
                               ?? throw new InvalidOperationException($"A {c.GetType().Name} could not be copied."));
        }
        return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), CopyOptions), CopyOptions)
               ?? throw new InvalidOperationException($"A {value.GetType().Name} could not be copied.");
    }

    /// <summary>Component → compact JSON including its "$type" (as in files: nulls omitted unless their default is not null).</summary>
    public static string SerializeComponent(Component component) => JsonSerializer.Serialize(component, CompactOptions);

    /// <summary>
    /// Component → compact JSON including its "$type" and every null, for exact undo snapshots (read back with
    /// <see cref="PopulateComponent"/>).
    /// </summary>
    public static string SnapshotComponent(Component component) => JsonSerializer.Serialize(component, CopyOptions);

    /// <summary>
    /// Overwrites every public settable property of <paramref name="target"/> with the values stored in
    /// <paramref name="json"/> (same concrete type). Used by undo of in-place component edits.
    /// </summary>
    public static void PopulateComponent(Component target, string json)
    {
        ArgumentNullException.ThrowIfNull(target);
        var source = JsonSerializer.Deserialize<Component>(json, CopyOptions)
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
