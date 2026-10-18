using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>Small helpers for Vulkan interop: result checks, alignment, native strings.</summary>
internal static unsafe class VkUtil
{
    /// <summary>Throws a <see cref="VulkanException"/> when <paramref name="result"/> is an error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check(Result result, string what)
    {
        if (result < Result.Success)
            Throw(result, what);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(Result result, string what) => throw new VulkanException(result, what);

    /// <summary>Rounds <paramref name="value"/> up to a multiple of <paramref name="alignment"/> (any positive alignment).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AlignUp(ulong value, ulong alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    /// <summary>Number of mip levels of a full chain for the given size.</summary>
    public static uint MipCount(uint width, uint height) => (uint)BitOperations.Log2(Math.Max(Math.Max(width, height), 1u)) + 1;

    /// <summary>Reads a NUL-terminated UTF-8 string from a fixed-size Vulkan char array.</summary>
    public static string FixedString(byte* chars, int maxLength)
    {
        int n = 0;
        while (n < maxLength && chars[n] != 0)
            n++;
        return Encoding.UTF8.GetString(chars, n);
    }

    /// <summary>Formats a packed Vulkan version as "major.minor.patch".</summary>
    public static string VersionString(uint version) =>
        $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";

    /// <summary>Packs a Vulkan API version.</summary>
    public static uint MakeVersion(uint major, uint minor, uint patch) => (major << 22) | (minor << 12) | patch;

    /// <summary>Major.minor of a packed version, for comparisons.</summary>
    public static uint MajorMinor(uint version) => version & ~0xFFFu;
}

/// <summary>
/// An array of NUL-terminated UTF-8 strings in unmanaged memory (extension and layer names for create-info structs).
/// Disposing frees the memory; the pointer must not be used afterwards.
/// </summary>
internal sealed unsafe class NativeStringArray : IDisposable
{
    private byte** _pointers;
    private readonly int _count;

    public NativeStringArray(IReadOnlyList<string> strings)
    {
        _count = strings.Count;
        _pointers = (byte**)NativeMemory.AllocZeroed((nuint)Math.Max(_count, 1), (nuint)sizeof(byte*));
        for (int i = 0; i < _count; i++)
            _pointers[i] = AllocUtf8(strings[i]);
    }

    public byte** Pointer => _pointers;

    public uint Count => (uint)_count;

    public static byte* AllocUtf8(string s)
    {
        int len = Encoding.UTF8.GetByteCount(s);
        var p = (byte*)NativeMemory.Alloc((nuint)len + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(p, len));
        p[len] = 0;
        return p;
    }

    public void Dispose()
    {
        if (_pointers == null)
            return;
        for (int i = 0; i < _count; i++)
            NativeMemory.Free(_pointers[i]);
        NativeMemory.Free(_pointers);
        _pointers = null;
    }
}
