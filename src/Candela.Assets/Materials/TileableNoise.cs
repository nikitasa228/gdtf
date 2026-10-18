namespace Candela.Assets;

/// <summary>
/// Deterministic, seamlessly tileable value noise for the procedural material textures. Coordinates are texture
/// coordinates in [0, 1); every octave's lattice period divides the tile, so the result wraps without seams.
/// </summary>
internal static class TileableNoise
{
    /// <summary>Uniform pseudo-random value in [0, 1) for an integer lattice point.</summary>
    public static float Hash01(int x, int y, uint seed)
    {
        uint h = unchecked((uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ seed * 0xCB1AB31Fu);
        h ^= h >> 13;
        h = unchecked(h * 0x5BD1E995u);
        h ^= h >> 15;
        h = unchecked(h * 0x27D4EB2Du);
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777216f;
    }

    /// <summary>Value noise in [0, 1] with <paramref name="periodX"/> × <paramref name="periodY"/> lattice cells per tile.</summary>
    public static float Value(float u, float v, int periodX, int periodY, uint seed)
    {
        float x = u * periodX, y = v * periodY;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int ix0 = Mod(x0, periodX), ix1 = Mod(x0 + 1, periodX);
        int iy0 = Mod(y0, periodY), iy1 = Mod(y0 + 1, periodY);
        float sx = Fade(fx), sy = Fade(fy);
        float a = Hash01(ix0, iy0, seed), b = Hash01(ix1, iy0, seed);
        float c = Hash01(ix0, iy1, seed), d = Hash01(ix1, iy1, seed);
        float top = a + (b - a) * sx, bottom = c + (d - c) * sx;
        return top + (bottom - top) * sy;
    }

    /// <summary>Fractal sum of <paramref name="octaves"/> value-noise octaves (period doubling, amplitude × gain), in [0, 1].</summary>
    public static float Fbm(float u, float v, int periodX, int periodY, int octaves, float gain, uint seed)
    {
        float sum = 0, amplitude = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amplitude * Value(u, v, periodX << o, periodY << o, seed + (uint)o * 1013u);
            norm += amplitude;
            amplitude *= gain;
        }
        return sum / norm;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }
}
