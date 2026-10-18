using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Protocols.ArtNet;

namespace Candela.Engine;

/// <summary>
/// Drives a patched GDTF fixture with the Art-Net test show (<see cref="ArtNetTestPattern"/>): the normalised look
/// (pan, tilt, dimmer, colour, zoom, focus) is mapped onto the fixture's own channel functions — pan/tilt/zoom/focus
/// across their physical range, dimmer to full, additive emitters by colour code (R, G, B, W …), subtractive CMY as the
/// complement — over the GDTF defaults, highlight values and an open shutter. One encoder per DMX break; the fixture's
/// break is written into its universe at its address. Allocation-free per frame.
/// </summary>
public sealed class GdtfTestPatternEncoder : ITestPatternEncoder
{
    private readonly FixtureDmxBuffer _buffer;
    private readonly FixtureDmxBuffer _base;
    private readonly int _breakIndex;
    private readonly int _address;
    private readonly (int Function, Channel Role)[] _functions;

    private GdtfTestPatternEncoder(FixtureProfile profile, FixtureDmxBuffer @base, (int, Channel)[] functions, int breakIndex, int universe, int address)
    {
        Profile = profile;
        _base = @base;
        _buffer = new FixtureDmxBuffer(profile);
        _functions = functions;
        _breakIndex = breakIndex;
        Universe = universe;
        _address = address;
    }

    private enum Channel
    {
        Pan,
        Tilt,
        Dimmer,
        Red,
        Green,
        Blue,
        White,
        Cyan,
        Magenta,
        Yellow,
        Zoom,
        Focus,
    }

    /// <summary>The fixture's profile.</summary>
    public FixtureProfile Profile { get; }

    /// <inheritdoc />
    public int Universe { get; }

    /// <summary>Encoders for every patched break of a fixture (empty when it is unpatched).</summary>
    public static IReadOnlyList<GdtfTestPatternEncoder> Create(FixtureProfile profile, FixtureComponent fixture)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(fixture);
        var @base = new FixtureDmxBuffer(profile);
        @base.ResetToDefaults();
        @base.ApplyHighlight();
        var functions = new List<(int, Channel)>();
        foreach (var ch in profile.Channels)
        {
            foreach (var f in profile.FunctionsOf(ch.Index))
            {
                Channel? role = f.Class.Kind switch
                {
                    AttributeKind.Pan => Channel.Pan,
                    AttributeKind.Tilt => Channel.Tilt,
                    AttributeKind.Dimmer => Channel.Dimmer,
                    AttributeKind.Zoom => Channel.Zoom,
                    AttributeKind.Focus => Channel.Focus,
                    AttributeKind.ColorAdd or AttributeKind.ColorRgb => f.Class.Code switch
                    {
                        "R" or "RED" => Channel.Red,
                        "G" or "GREEN" => Channel.Green,
                        "B" or "BLUE" => Channel.Blue,
                        "W" or "WW" or "CW" or "WHITE" => Channel.White,
                        _ => null,
                    },
                    AttributeKind.ColorSub => f.Class.Code switch
                    {
                        "C" => Channel.Cyan,
                        "M" => Channel.Magenta,
                        "Y" => Channel.Yellow,
                        _ => null,
                    },
                    _ => null,
                };
                if (f.Class.Kind == AttributeKind.Shutter)
                {
                    foreach (var s in f.Sets)
                    {
                        if ((s.Flags & ChannelSetFlags.Open) == 0) continue;
                        @base.SetValue(ch.Index, s.DmxFrom);
                        break;
                    }
                }
                if (role is not { } r) continue;
                functions.Add((f.Index, r));
                break;
            }
        }
        var encoders = new List<GdtfTestPatternEncoder>();
        var array = functions.ToArray();
        foreach (var address in fixture.Addresses)
        {
            int breakIndex = profile.BreakIndexOf(address.Break);
            if (breakIndex < 0 && profile.Breaks.Count == 1) breakIndex = 0;
            if (breakIndex < 0 || address.Universe < 1 || address.Address is < 1 or > 512) continue;
            encoders.Add(new GdtfTestPatternEncoder(profile, @base, array, breakIndex, address.Universe, address.Address));
        }
        return encoders;
    }

    /// <summary>
    /// A test show over every patched fixture of the engine's scene (fixtures whose GDTF cannot be resolved are skipped).
    /// </summary>
    public static ArtNetTestPattern ForScene(StageEngine engine, TestPatternSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var encoders = new List<ITestPatternEncoder>();
        foreach (var (_, fixture) in engine.Scene.Query<FixtureComponent>())
        {
            if (engine.Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode).Profile is not { } profile) continue;
            encoders.AddRange(Create(profile, fixture));
        }
        return new ArtNetTestPattern(encoders, settings);
    }

    /// <inheritdoc />
    public void Encode(in TestPatternLook look, Span<byte> universeSlots)
    {
        _buffer.CopyFrom(_base);
        var functions = Profile.Functions;
        foreach (var (index, role) in _functions)
        {
            var f = functions[index];
            float t = role switch
            {
                Channel.Pan => look.Pan,
                Channel.Tilt => look.Tilt,
                Channel.Dimmer => look.Dimmer,
                Channel.Red => look.Red,
                Channel.Green => look.Green,
                Channel.Blue => look.Blue,
                Channel.White => look.White,
                Channel.Cyan => 1f - look.Red,
                Channel.Magenta => 1f - look.Green,
                Channel.Yellow => 1f - look.Blue,
                Channel.Zoom => look.Zoom,
                _ => look.Focus,
            };
            t = float.IsFinite(t) ? Math.Clamp(t, 0f, 1f) : 0f;
            DmxEncoder.EncodeFunction(_buffer, index, f.MinValue + t * (f.MaxValue - f.MinValue));
        }
        var slots = _buffer.GetBreak(_breakIndex);
        int start = _address - 1;
        int n = Math.Min(slots.Length, universeSlots.Length - start);
        if (n > 0) slots[..n].CopyTo(universeSlots[start..]);
    }
}
