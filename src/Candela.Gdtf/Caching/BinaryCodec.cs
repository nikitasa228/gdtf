using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Candela.Core.SceneGraph.Components;

namespace Candela.Gdtf;

/// <summary>
/// Little-endian binary writer for the fixture type cache: 7-bit encoded integers, raw floats and a string table
/// (each distinct string is written once; later uses refer to it by index).
/// </summary>
internal sealed class CacheWriter
{
    private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
    private byte[] _buffer;
    private int _length;

    public CacheWriter(int capacity = 1 << 16) => _buffer = new byte[capacity];

    /// <summary>The bytes written so far.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            long size = System.Math.Max((long)_buffer.Length * 2, (long)_length + count);
            if (size > Array.MaxLength) throw new InvalidOperationException("Fixture type too large to cache.");
            Array.Resize(ref _buffer, (int)size);
        }
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public void Byte(byte value) => Reserve(1)[0] = value;

    public void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    /// <summary>Unsigned 7-bit encoded integer.</summary>
    public void UInt(uint value)
    {
        while (value >= 0x80)
        {
            Byte((byte)(value | 0x80));
            value >>= 7;
        }
        Byte((byte)value);
    }

    /// <summary>Signed integer (zig-zag, 7-bit encoded).</summary>
    public void Int(int value) => UInt((uint)((value << 1) ^ (value >> 31)));

    /// <summary>A count of following items.</summary>
    public void Count(int count) => UInt((uint)count);

    /// <summary>An enum value (every enum of the model has an <see cref="int"/> underlying type).</summary>
    public void Enum<T>(T value) where T : struct, System.Enum => Int(Unsafe.As<T, int>(ref value));

    public void Float(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);

    public void FloatOpt(float? value)
    {
        Bool(value.HasValue);
        if (value is { } v) Float(v);
    }

    public void Long(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    public void Guid(Guid value) => value.TryWriteBytes(Reserve(16));

    /// <summary>A string (null allowed): 0 = null, 1 = new string follows, n ≥ 2 = string number n − 2.</summary>
    public void String(string? value)
    {
        if (value is null)
        {
            UInt(0);
            return;
        }
        if (_strings.TryGetValue(value, out int index))
        {
            UInt((uint)index + 2);
            return;
        }
        _strings.Add(value, _strings.Count);
        UInt(1);
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        var tmp = max <= 1024 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(value, tmp);
        UInt((uint)n);
        tmp[..n].CopyTo(Reserve(n));
    }

    public void Color(CieColor c)
    {
        Float(c.X);
        Float(c.Y);
        Float(c.BigY);
    }

    public void ColorOpt(CieColor? c)
    {
        Bool(c.HasValue);
        if (c is { } v) Color(v);
    }

    public void Vector2(Vector2 v)
    {
        Float(v.X);
        Float(v.Y);
    }

    public void Vector3(Vector3 v)
    {
        Float(v.X);
        Float(v.Y);
        Float(v.Z);
    }

    public void Matrix(Matrix4x4 m)
    {
        Float(m.M11); Float(m.M12); Float(m.M13); Float(m.M14);
        Float(m.M21); Float(m.M22); Float(m.M23); Float(m.M24);
        Float(m.M31); Float(m.M32); Float(m.M33); Float(m.M34);
        Float(m.M41); Float(m.M42); Float(m.M43); Float(m.M44);
    }

    public void Matrix(Matrix3x2 m)
    {
        Float(m.M11); Float(m.M12);
        Float(m.M21); Float(m.M22);
        Float(m.M31); Float(m.M32);
    }

    /// <summary>A DMX value; <c>default(DmxValue)</c> (byte count 0) is kept as such.</summary>
    public void Dmx(DmxValue v)
    {
        Byte((byte)v.ByteCount);
        UInt(v.Value);
        Byte((byte)v.Conversion);
    }

    public void DmxOpt(DmxValue? v)
    {
        Bool(v.HasValue);
        if (v is { } value) Dmx(value);
    }

    public void Dictionary(Dictionary<string, string> d)
    {
        Count(d.Count);
        foreach (var (k, v) in d)
        {
            String(k);
            String(v);
        }
    }
}

/// <summary>
/// Reader for <see cref="CacheWriter"/> data. Every count, length and index is checked against what is left, so a
/// damaged or hostile cache file fails with <see cref="InvalidDataException"/> instead of allocating without bound.
/// </summary>
internal sealed class CacheReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private readonly List<string> _strings = new();
    private int _pos;

    public CacheReader(byte[] data, int offset, int count)
    {
        _data = data;
        _pos = offset;
        _end = offset + count;
    }

    public bool AtEnd => _pos == _end;

    private int Remaining => _end - _pos;

    private static InvalidDataException Bad(string what) => new($"Fixture type cache data is damaged ({what}).");

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw Bad("unexpected end");
        var span = _data.AsSpan(_pos, count);
        _pos += count;
        return span;
    }

    public byte Byte() => Take(1)[0];

    public bool Bool() => Byte() switch
    {
        0 => false,
        1 => true,
        _ => throw Bad("boolean"),
    };

    public uint UInt()
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = Byte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw Bad("integer");
    }

    public int Int()
    {
        uint z = UInt();
        return (int)(z >> 1) ^ -(int)(z & 1);
    }

    /// <summary>A count of following items, each at least <paramref name="minBytesPerItem"/> bytes long.</summary>
    public int Count(int minBytesPerItem = 1)
    {
        uint n = UInt();
        if (n > (uint)Remaining / (uint)System.Math.Max(1, minBytesPerItem)) throw Bad("count");
        return (int)n;
    }

    /// <summary>An enum value, checked against the enum's defined values (flags: against the union of all flags).</summary>
    public T Enum<T>() where T : struct, System.Enum
    {
        int v = Int();
        if (!EnumRange<T>.IsValid(v)) throw Bad($"{typeof(T).Name} value {v}");
        return Unsafe.As<int, T>(ref v);
    }

    /// <summary>Valid values of an enum with an <see cref="int"/> underlying type, computed once per type.</summary>
    private static class EnumRange<T> where T : struct, System.Enum
    {
        private static readonly HashSet<int> Values = System.Enum.GetValues<T>().Select(x => Unsafe.As<T, int>(ref x)).ToHashSet();
        private static readonly bool IsFlags = typeof(T).IsDefined(typeof(FlagsAttribute), false);
        private static readonly int AllFlags = Values.Aggregate(0, (a, b) => a | b);

        public static bool IsValid(int v) => IsFlags ? (v & ~AllFlags) == 0 : Values.Contains(v);
    }

    public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public float? FloatOpt() => Bool() ? Float() : null;

    public long Long() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public Guid Guid() => new(Take(16));

    public string? StringOpt()
    {
        uint code = UInt();
        switch (code)
        {
            case 0:
                return null;
            case 1:
                int n = Count();
                string s = Encoding.UTF8.GetString(Take(n));
                _strings.Add(s);
                return s;
            default:
                uint index = code - 2;
                if (index >= (uint)_strings.Count) throw Bad("string index");
                return _strings[(int)index];
        }
    }

    public string String() => StringOpt() ?? throw Bad("missing string");

    public CieColor Color() => new(Float(), Float(), Float());

    public CieColor? ColorOpt() => Bool() ? Color() : null;

    public Vector2 Vector2() => new(Float(), Float());

    public Vector3 Vector3() => new(Float(), Float(), Float());

    public Matrix4x4 Matrix4() => new(
        Float(), Float(), Float(), Float(),
        Float(), Float(), Float(), Float(),
        Float(), Float(), Float(), Float(),
        Float(), Float(), Float(), Float());

    public Matrix3x2 Matrix3x2() => new(Float(), Float(), Float(), Float(), Float(), Float());

    public DmxValue Dmx()
    {
        int bytes = Byte();
        uint value = UInt();
        byte conversion = Byte();
        if (conversion > (byte)DmxConversion.ByteShifting) throw Bad("DMX conversion");
        if (bytes == 0)
        {
            if (value != 0) throw Bad("DMX value");
            return default;
        }
        if (bytes > DmxValue.MaxByteCount || value > DmxValue.MaxFor(bytes)) throw Bad("DMX value");
        return new DmxValue(value, bytes, (DmxConversion)conversion);
    }

    public DmxValue? DmxOpt() => Bool() ? Dmx() : null;

    public void Dictionary(Dictionary<string, string> target)
    {
        int n = Count(2);
        for (int i = 0; i < n; i++)
        {
            string k = String();
            string v = String();
            target[k] = v;
        }
    }

    /// <summary>A link written as index + 1 (0 = null) into <paramref name="items"/>.</summary>
    public T? Link<T>(List<T> items) where T : class
    {
        uint code = UInt();
        if (code == 0) return null;
        if (code - 1 >= (uint)items.Count) throw Bad($"{typeof(T).Name} link");
        return items[(int)(code - 1)];
    }
}
