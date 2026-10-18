namespace Candela.Assets;

/// <summary>sRGB transfer function (IEC 61966-2-1) used for 8-bit colour textures and PNG output.</summary>
public static class ColorEncoding
{
    /// <summary>Linear value (0 … 1) → sRGB-encoded value (0 … 1).</summary>
    public static float LinearToSrgb(float linear)
    {
        if (!(linear > 0f)) return 0f;
        if (linear >= 1f) return 1f;
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>sRGB-encoded value (0 … 1) → linear value (0 … 1).</summary>
    public static float SrgbToLinear(float encoded)
    {
        if (!(encoded > 0f)) return 0f;
        if (encoded >= 1f) return 1f;
        return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    private static readonly float[] s_srgbByteToLinear = BuildTable();

    /// <summary>Linear value of an sRGB-encoded byte (table lookup).</summary>
    public static float SrgbByteToLinear(byte encoded) => s_srgbByteToLinear[encoded];

    /// <summary>Linear value (0 … 1) → nearest sRGB-encoded byte.</summary>
    public static byte LinearToSrgbByte(float linear) => (byte)MathF.Round(LinearToSrgb(linear) * 255f);

    private static float[] BuildTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = SrgbToLinear(i / 255f);
        return t;
    }
}
