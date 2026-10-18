namespace Candela.Protocols.ArtNet;

/// <summary>
/// What the test show wants one fixture to do at one instant, in normalised units (0..1 for every field). Encoders turn
/// it into DMX for a concrete fixture.
/// </summary>
/// <param name="Pan">Pan position 0..1 (0.5 = centre of the fixture's range).</param>
/// <param name="Tilt">Tilt position 0..1 (0.5 = centre).</param>
/// <param name="Dimmer">Intensity 0..1.</param>
/// <param name="Red">Red emitter / colour component 0..1.</param>
/// <param name="Green">Green component 0..1.</param>
/// <param name="Blue">Blue component 0..1.</param>
/// <param name="White">White component 0..1.</param>
/// <param name="Zoom">Zoom 0..1 (narrow → wide).</param>
/// <param name="Focus">Focus 0..1.</param>
public readonly record struct TestPatternLook(
    float Pan,
    float Tilt,
    float Dimmer,
    float Red,
    float Green,
    float Blue,
    float White,
    float Zoom,
    float Focus);

/// <summary>
/// Turns a <see cref="TestPatternLook"/> into DMX for one fixture. <see cref="RawChannelEncoder"/> covers raw channel
/// lists; the engine can plug in GDTF-mode encoders from Candela.Dmx so the pattern drives any patched fixture.
/// </summary>
public interface ITestPatternEncoder
{
    /// <summary>Internal 1-based universe the fixture is patched to.</summary>
    int Universe { get; }

    /// <summary>Writes the look into the 512 slots of <see cref="Universe"/> (slot 1 = index 0). Must not allocate.</summary>
    void Encode(in TestPatternLook look, Span<byte> universeSlots);
}

/// <summary>Meaning of one raw DMX channel for the test pattern.</summary>
public enum TestPatternRole
{
    /// <summary>Master intensity.</summary>
    Dimmer,

    /// <summary>Pan position.</summary>
    Pan,

    /// <summary>Tilt position.</summary>
    Tilt,

    /// <summary>Red emitter (or additive red).</summary>
    Red,

    /// <summary>Green emitter.</summary>
    Green,

    /// <summary>Blue emitter.</summary>
    Blue,

    /// <summary>White emitter.</summary>
    White,

    /// <summary>Cyan flag (subtractive; written as 1 − red).</summary>
    Cyan,

    /// <summary>Magenta flag (written as 1 − green).</summary>
    Magenta,

    /// <summary>Yellow flag (written as 1 − blue).</summary>
    Yellow,

    /// <summary>Zoom.</summary>
    Zoom,

    /// <summary>Focus.</summary>
    Focus,

    /// <summary>A channel held at a fixed value (shutter open, mode/control channels, …).</summary>
    Fixed,
}

/// <summary>One raw channel of a test-pattern fixture.</summary>
/// <param name="Role">What the channel controls.</param>
/// <param name="Offset">0-based offset from the fixture's start address (coarse byte).</param>
/// <param name="FineOffset">0-based offset of the fine byte for 16-bit channels, or −1.</param>
/// <param name="Value">Value for <see cref="TestPatternRole.Fixed"/> channels.</param>
public readonly record struct TestPatternChannel(TestPatternRole Role, int Offset, int FineOffset = -1, byte Value = 0);

/// <summary>A fixture driven through a raw channel list (no GDTF needed).</summary>
/// <param name="Universe">Internal 1-based universe.</param>
/// <param name="Address">1-based start address.</param>
/// <param name="Channels">Channel layout.</param>
public sealed record TestPatternFixture(int Universe, int Address, IReadOnlyList<TestPatternChannel> Channels)
{
    /// <summary>A common 16-bit moving-head layout: pan, pan fine, tilt, tilt fine, shutter (open), dimmer, R, G, B, W,
    /// zoom, focus — 12 channels.</summary>
    public static TestPatternFixture MovingHead(int universe, int address) => new(universe, address, new TestPatternChannel[]
    {
        new(TestPatternRole.Pan, 0, 1),
        new(TestPatternRole.Tilt, 2, 3),
        new(TestPatternRole.Fixed, 4, Value: 255),
        new(TestPatternRole.Dimmer, 5),
        new(TestPatternRole.Red, 6),
        new(TestPatternRole.Green, 7),
        new(TestPatternRole.Blue, 8),
        new(TestPatternRole.White, 9),
        new(TestPatternRole.Zoom, 10),
        new(TestPatternRole.Focus, 11),
    });

    /// <summary>An RGBW par with dimmer: dimmer, R, G, B, W — 5 channels.</summary>
    public static TestPatternFixture RgbwPar(int universe, int address) => new(universe, address, new TestPatternChannel[]
    {
        new(TestPatternRole.Dimmer, 0),
        new(TestPatternRole.Red, 1),
        new(TestPatternRole.Green, 2),
        new(TestPatternRole.Blue, 3),
        new(TestPatternRole.White, 4),
    });

    /// <summary>Number of DMX channels the layout occupies.</summary>
    public int Footprint
    {
        get
        {
            int max = 0;
            foreach (var c in Channels) max = Math.Max(max, Math.Max(c.Offset, c.FineOffset) + 1);
            return max;
        }
    }
}

/// <summary>Encodes a <see cref="TestPatternFixture"/> (raw channel list). Channels beyond slot 512 are skipped.</summary>
public sealed class RawChannelEncoder : ITestPatternEncoder
{
    private readonly TestPatternChannel[] _channels;
    private readonly int _start;

    /// <summary>Creates the encoder; validates universe and address.</summary>
    public RawChannelEncoder(TestPatternFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.Universe < 1) throw new ArgumentOutOfRangeException(nameof(fixture), fixture.Universe, "Universes are 1-based.");
        if (fixture.Address is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(fixture), fixture.Address, "Addresses are 1..512.");
        Fixture = fixture;
        Universe = fixture.Universe;
        _start = fixture.Address - 1;
        _channels = fixture.Channels.ToArray();
    }

    /// <summary>The fixture being encoded.</summary>
    public TestPatternFixture Fixture { get; }

    /// <inheritdoc />
    public int Universe { get; }

    /// <inheritdoc />
    public void Encode(in TestPatternLook look, Span<byte> universeSlots)
    {
        foreach (var c in _channels)
        {
            float v = c.Role switch
            {
                TestPatternRole.Dimmer => look.Dimmer,
                TestPatternRole.Pan => look.Pan,
                TestPatternRole.Tilt => look.Tilt,
                TestPatternRole.Red => look.Red,
                TestPatternRole.Green => look.Green,
                TestPatternRole.Blue => look.Blue,
                TestPatternRole.White => look.White,
                TestPatternRole.Cyan => 1 - look.Red,
                TestPatternRole.Magenta => 1 - look.Green,
                TestPatternRole.Yellow => 1 - look.Blue,
                TestPatternRole.Zoom => look.Zoom,
                TestPatternRole.Focus => look.Focus,
                _ => c.Value / 255f,
            };
            if (c.Role == TestPatternRole.Fixed)
            {
                Write(universeSlots, _start + c.Offset, c.Value);
                continue;
            }
            int value16 = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f);
            if (c.FineOffset >= 0)
            {
                Write(universeSlots, _start + c.Offset, (byte)(value16 >> 8));
                Write(universeSlots, _start + c.FineOffset, (byte)value16);
            }
            else
            {
                Write(universeSlots, _start + c.Offset, (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f));
            }
        }
    }

    private static void Write(Span<byte> slots, int index, byte value)
    {
        if ((uint)index < (uint)slots.Length) slots[index] = value;
    }
}

/// <summary>Which effects the test show runs and how fast.</summary>
public sealed record TestPatternSettings
{
    /// <summary>Global speed multiplier (1 = default tempo: 8 s pan cycle, 5 s tilt, 4 s dimmer wave, 12 s colour).</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Pan/tilt sine sweeps (fixtures out of phase with each other).</summary>
    public bool PanTilt { get; init; } = true;

    /// <summary>Hue chase across the fixtures.</summary>
    public bool ColourChase { get; init; } = true;

    /// <summary>Intensity wave running along the fixtures.</summary>
    public bool DimmerWave { get; init; } = true;

    /// <summary>Slow zoom breathing.</summary>
    public bool Zoom { get; init; } = true;

    /// <summary>Pan sweep amplitude as a fraction of the range (0.35 = ±35 % around centre).</summary>
    public double PanAmplitude { get; init; } = 0.35;

    /// <summary>Tilt sweep amplitude as a fraction of the range.</summary>
    public double TiltAmplitude { get; init; } = 0.25;
}

/// <summary>
/// A moving test show for fixtures without a console: pan/tilt sine sweeps, a colour chase, a dimmer wave and zoom
/// breathing, each fixture phase-shifted by its index. <see cref="Render"/> fills one 512-slot buffer per universe;
/// <see cref="ArtNetTestConsole"/> sends them as Art-Net. Rendering is deterministic in time and allocation-free.
/// </summary>
public sealed class ArtNetTestPattern
{
    private readonly ITestPatternEncoder[] _encoders;
    private readonly Dictionary<int, byte[]> _buffers = new();
    private readonly int[] _universes;

    /// <summary>Creates the show for any encoders (raw channel lists or GDTF-based ones).</summary>
    public ArtNetTestPattern(IEnumerable<ITestPatternEncoder> fixtures, TestPatternSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        _encoders = fixtures.ToArray();
        Settings = settings ?? new TestPatternSettings();
        foreach (var e in _encoders)
        {
            if (e.Universe < 1) throw new ArgumentException("Universes are 1-based.", nameof(fixtures));
            if (!_buffers.ContainsKey(e.Universe)) _buffers[e.Universe] = new byte[512];
        }
        _universes = _buffers.Keys.Order().ToArray();
    }

    /// <summary>Creates the show for raw channel-list fixtures.</summary>
    public static ArtNetTestPattern ForFixtures(IEnumerable<TestPatternFixture> fixtures, TestPatternSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        return new ArtNetTestPattern(fixtures.Select(f => (ITestPatternEncoder)new RawChannelEncoder(f)), settings);
    }

    /// <summary>
    /// A ready-made rig: <paramref name="movingHeads"/> 12-channel moving heads followed by <paramref name="pars"/> RGBW
    /// pars, packed consecutively from address 1 of <paramref name="firstUniverse"/> (continuing in the next universe
    /// when a fixture would cross slot 512).
    /// </summary>
    public static ArtNetTestPattern DemoRig(int movingHeads = 8, int pars = 8, int firstUniverse = 1, TestPatternSettings? settings = null)
    {
        var fixtures = new List<TestPatternFixture>();
        int universe = firstUniverse, address = 1;
        void Place(Func<int, int, TestPatternFixture> make)
        {
            var probe = make(universe, address);
            if (address + probe.Footprint - 1 > 512)
            {
                universe++;
                address = 1;
                probe = make(universe, address);
            }
            fixtures.Add(probe);
            address += probe.Footprint;
        }
        for (int i = 0; i < movingHeads; i++) Place(TestPatternFixture.MovingHead);
        for (int i = 0; i < pars; i++) Place(TestPatternFixture.RgbwPar);
        return ForFixtures(fixtures, settings);
    }

    /// <summary>The effect settings.</summary>
    public TestPatternSettings Settings { get; }

    /// <summary>Universes the show writes, ascending.</summary>
    public IReadOnlyList<int> Universes => _universes;

    /// <summary>Number of fixtures.</summary>
    public int FixtureCount => _encoders.Length;

    /// <summary>Evaluates the show at <paramref name="timeSeconds"/> into the universe buffers.</summary>
    public void Render(double timeSeconds)
    {
        foreach (var buffer in _buffers.Values) Array.Clear(buffer);
        for (int i = 0; i < _encoders.Length; i++)
        {
            var e = _encoders[i];
            var look = LookAt(timeSeconds, i, _encoders.Length, Settings);
            e.Encode(look, _buffers[e.Universe]);
        }
    }

    /// <summary>The 512 slots of <paramref name="universe"/> from the last <see cref="Render"/> (empty when the show
    /// does not use it).</summary>
    public ReadOnlySpan<byte> GetUniverse(int universe) =>
        _buffers.TryGetValue(universe, out var b) ? b : ReadOnlySpan<byte>.Empty;

    /// <summary>The look of fixture <paramref name="index"/> of <paramref name="count"/> at time <paramref name="t"/>
    /// (seconds). Pure function.</summary>
    public static TestPatternLook LookAt(double t, int index, int count, TestPatternSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double phase = count > 0 ? index / (double)count : 0;
        double s = t * settings.Speed;
        const double Tau = 2 * Math.PI;

        double pan = 0.5, tilt = 0.5, dimmer = 1, zoom = 0.5;
        if (settings.PanTilt)
        {
            pan = 0.5 + settings.PanAmplitude * Math.Sin(Tau * (s / 8.0 + phase));
            tilt = 0.5 + settings.TiltAmplitude * Math.Sin(Tau * (s / 5.0 + phase * 0.5));
        }
        if (settings.DimmerWave) dimmer = 0.55 + 0.45 * Math.Sin(Tau * (s / 4.0 - phase));
        if (settings.Zoom) zoom = 0.5 + 0.5 * Math.Sin(Tau * s / 10.0);

        double r = 1, g = 1, b = 1, w = 1;
        if (settings.ColourChase)
        {
            double hue = Frac(s / 12.0 + phase);
            HueToRgb(hue, out r, out g, out b);
            w = 0;
        }
        return new TestPatternLook((float)pan, (float)tilt, (float)dimmer, (float)r, (float)g, (float)b, (float)w, (float)zoom, 0.5f);
    }

    private static double Frac(double x) => x - Math.Floor(x);

    private static void HueToRgb(double hue, out double r, out double g, out double b)
    {
        double h = hue * 6;
        int sector = (int)Math.Floor(h) % 6;
        double f = h - Math.Floor(h);
        (r, g, b) = sector switch
        {
            0 => (1.0, f, 0.0),
            1 => (1.0 - f, 1.0, 0.0),
            2 => (0.0, 1.0, f),
            3 => (0.0, 1.0 - f, 1.0),
            4 => (f, 0.0, 1.0),
            _ => (1.0, 0.0, 1.0 - f),
        };
    }
}
