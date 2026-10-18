namespace Candela.Dmx;

/// <summary>
/// Deterministic periodic functions of time for strobes, pulses and random effects: the same time, frequency and seed
/// always give the same value, so a frame rendered twice (or offline from recorded DMX) looks the same.
/// </summary>
public static class Waveforms
{
    /// <summary>Default flash length of a rectangular strobe, s (capped at half the period).</summary>
    public const float DefaultFlashDuration = 0.05f;

    /// <summary>
    /// Shutter opening 0 … 1 of <paramref name="waveform"/> at <paramref name="time"/> (s) for frequency
    /// <paramref name="frequency"/> (Hz). <paramref name="flashDuration"/> (s, NaN = default) sets the on-time of
    /// rectangular flashes; <paramref name="seed"/> decorrelates random effects between fixtures. Frequencies ≤ 0.01 Hz
    /// or non-finite input give 1 (open).
    /// </summary>
    public static float Evaluate(Waveform waveform, double time, float frequency, float flashDuration = float.NaN, uint seed = 0)
    {
        if (waveform == Waveform.None || !(frequency > 0.01f) || !float.IsFinite(frequency) || !double.IsFinite(time)) return 1f;
        double cycles = time * frequency;
        double k = Math.Floor(cycles);
        float phase = (float)(cycles - k);
        long period = (long)k;
        float duty = Duty(frequency, flashDuration);
        switch (waveform)
        {
            case Waveform.Strobe:
                return phase < duty ? 1f : 0f;
            case Waveform.Pulse:
                return 1f - MathF.Abs(2f * phase - 1f);
            case Waveform.PulseOpen:
                return phase;
            case Waveform.PulseClose:
                return 1f - phase;
            case Waveform.Sine:
                return 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * phase);
            case Waveform.Burst:
                {
                    // three short flashes in the first 40 % of each period
                    if (phase >= 0.4f) return 0f;
                    float sub = phase / 0.4f * 3f;
                    return sub - MathF.Floor(sub) < 0.45f ? 1f : 0f;
                }
            case Waveform.Random or Waveform.RandomPulse or Waveform.RandomPulseOpen or Waveform.RandomPulseClose:
                {
                    // one event per period at a random start; some periods stay dark so the rhythm is irregular
                    if (Hash01(seed, period, 0x51) < 0.25f) return 0f;
                    float width = waveform == Waveform.Random ? duty : 0.5f;
                    float start = Hash01(seed, period, 0x17) * (1f - width);
                    float local = (phase - start) / width;
                    if (local is < 0f or >= 1f) return 0f;
                    return waveform switch
                    {
                        Waveform.Random => 1f,
                        Waveform.RandomPulseOpen => local,
                        Waveform.RandomPulseClose => 1f - local,
                        _ => 1f - MathF.Abs(2f * local - 1f),
                    };
                }
            default:
                return 1f;
        }
    }

    /// <summary>On-time fraction of a rectangular flash: flash length × frequency, within 2 % … 50 %.</summary>
    public static float Duty(float frequency, float flashDuration)
    {
        float d = float.IsFinite(flashDuration) && flashDuration > 0f ? flashDuration : DefaultFlashDuration;
        return Math.Clamp(d * frequency, 0.02f, 0.5f);
    }

    /// <summary>Sinusoidal oscillation −1 … 1 at <paramref name="frequency"/> Hz (shakes); 0 for non-positive frequencies.</summary>
    public static float Oscillation(double time, float frequency)
    {
        if (!(frequency > 0f) || !float.IsFinite(frequency) || !double.IsFinite(time)) return 0f;
        double c = time * frequency;
        return MathF.Sin(2f * MathF.PI * (float)(c - Math.Floor(c)));
    }

    /// <summary>Deterministic pseudo-random number in [0, 1) for a seed, an integer index and a stream id.</summary>
    public static float Hash01(uint seed, long index, uint stream)
    {
        ulong z = (ulong)index * 0x9E3779B97F4A7C15UL ^ ((ulong)seed << 32 | stream);
        z ^= z >> 30;
        z *= 0xBF58476D1CE4E5B9UL;
        z ^= z >> 27;
        z *= 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 40) * (1f / (1 << 24));
    }
}
