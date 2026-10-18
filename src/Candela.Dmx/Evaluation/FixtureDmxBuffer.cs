namespace Candela.Dmx;

/// <summary>
/// The DMX input of one fixture: the slots of its footprint in each DMX break (break-relative, slot 1 at index 0) plus
/// the values of its virtual channels (channels without an address, e.g. per-pixel virtual dimmers). Written by the
/// engine (slices of universes), by <see cref="DmxEncoder"/> (manual control, tests) or directly; read by
/// <see cref="FixtureEvaluator"/>.
/// </summary>
public sealed class FixtureDmxBuffer
{
    private readonly byte[][] _breaks;
    private readonly uint[] _virtual;

    /// <summary>Creates a buffer for <paramref name="profile"/> holding the profile's default values.</summary>
    public FixtureDmxBuffer(FixtureProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _breaks = new byte[profile.Breaks.Count][];
        for (int i = 0; i < _breaks.Length; i++) _breaks[i] = new byte[profile.Breaks[i].Footprint];
        _virtual = new uint[profile.Channels.Count];
        ResetToDefaults();
    }

    /// <summary>The profile the buffer is laid out for.</summary>
    public FixtureProfile Profile { get; }

    /// <summary>Number of breaks (same order as <see cref="FixtureProfile.Breaks"/>).</summary>
    public int BreakCount => _breaks.Length;

    /// <summary>Incremented by every write through this API (direct span writes do not count).</summary>
    public long Version { get; private set; }

    /// <summary>Slots of break <paramref name="breakIndex"/> (index into <see cref="FixtureProfile.Breaks"/>), slot 1 at index 0.</summary>
    public Span<byte> GetBreak(int breakIndex) => _breaks[breakIndex];

    /// <summary>Raw value of a channel at its resolution (bytes combined coarse first; virtual channels from their store).</summary>
    public uint GetValue(int channel)
    {
        var ch = Profile.Channels[channel];
        if (ch.IsVirtual || ch.BreakIndex < 0) return _virtual[channel];
        var slots = _breaks[ch.BreakIndex];
        var offsets = ch.Instance.Offsets;
        uint v = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            int o = offsets[i] - 1;
            v = (v << 8) | ((uint)o < (uint)slots.Length ? slots[o] : 0u);
        }
        return v;
    }

    /// <summary>Writes a channel value (clamped to the channel's resolution) into its slots, coarse byte first.</summary>
    public void SetValue(int channel, uint value)
    {
        var ch = Profile.Channels[channel];
        value = Math.Min(value, ch.MaxValue);
        Version++;
        if (ch.IsVirtual || ch.BreakIndex < 0)
        {
            _virtual[channel] = value;
            return;
        }
        var slots = _breaks[ch.BreakIndex];
        var offsets = ch.Instance.Offsets;
        for (int i = offsets.Length - 1; i >= 0; i--)
        {
            int o = offsets[i] - 1;
            if ((uint)o < (uint)slots.Length) slots[o] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    /// <summary>Sets every channel to its default value (and slots not used by any channel to 0).</summary>
    public void ResetToDefaults()
    {
        foreach (var b in _breaks) Array.Clear(b);
        foreach (var ch in Profile.Channels) SetValue(ch.Index, ch.DefaultValue);
        Version++;
    }

    /// <summary>Sets every channel with a highlight value to it (others keep their value).</summary>
    public void ApplyHighlight()
    {
        foreach (var ch in Profile.Channels)
            if (ch.HighlightValue is { } h) SetValue(ch.Index, h);
    }

    /// <summary>
    /// Copies the fixture's slots of one break out of a universe: <paramref name="universe"/> holds 512 slots and the
    /// break starts at 1-based <paramref name="address"/>. Slots beyond the universe read as 0. Returns true when the
    /// content changed.
    /// </summary>
    public bool CopyFromUniverse(int breakIndex, ReadOnlySpan<byte> universe, int address)
    {
        var dest = _breaks[breakIndex];
        int start = address - 1;
        bool changed = false;
        for (int i = 0; i < dest.Length; i++)
        {
            int s = start + i;
            byte v = (uint)s < (uint)universe.Length ? universe[s] : (byte)0;
            if (dest[i] != v)
            {
                dest[i] = v;
                changed = true;
            }
        }
        if (changed) Version++;
        return changed;
    }

    /// <summary>Copies all values from another buffer of the same profile.</summary>
    public void CopyFrom(FixtureDmxBuffer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!ReferenceEquals(other.Profile, Profile)) throw new ArgumentException("Buffers belong to different profiles.", nameof(other));
        for (int i = 0; i < _breaks.Length; i++) other._breaks[i].CopyTo(_breaks[i], 0);
        other._virtual.CopyTo(_virtual, 0);
        Version++;
    }
}
