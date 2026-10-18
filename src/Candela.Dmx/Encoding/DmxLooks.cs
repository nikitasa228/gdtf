using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx;

/// <summary>
/// Ready-made DMX states of one fixture, built from its GDTF channel functions: what a lighting programmer would call
/// "open white" or "locate". Used by the engine's looks, the test console and tests.
/// </summary>
public static class DmxLooks
{
    /// <summary>
    /// Writes "open white" into <paramref name="buffer"/>: GDTF defaults, then the highlight values, then every
    /// intensity channel at its brightest value (the dimmer function and value with the highest level, never a range
    /// named "Closed"/"Blackout"), shutter and strobe channels open without strobing, additive emitters at full,
    /// indirect RGB white (R, G, B full; C, M, Y off), HSB/CIE brightness full and saturation off, and the zoom at
    /// <paramref name="zoom"/> of its range (0 narrowest … 1 widest). Other channels keep their default or highlight
    /// value. Returns the channels that were written (highlight channels included), ascending.
    /// </summary>
    public static IReadOnlyList<int> OpenWhite(FixtureDmxBuffer buffer, float zoom = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var p = buffer.Profile;
        zoom = float.IsFinite(zoom) ? Math.Clamp(zoom, 0f, 1f) : 0.5f;
        buffer.ResetToDefaults();
        buffer.ApplyHighlight();
        var written = new SortedSet<int>();
        foreach (var ch in p.Channels)
            if (ch.HighlightValue is not null) written.Add(ch.Index);

        foreach (var ch in p.Channels)
        {
            var functions = p.FunctionsOf(ch.Index);
            if (functions.Length == 0) continue;
            var role = Role(ch, functions);
            if (role is not (AttributeKind.Dimmer or AttributeKind.Shutter))
            {
                // a highlight or default value that closes the light from a channel of another attribute (a shutter
                // function on an effects channel) is opened as well
                int active = FixtureEvaluator.SelectFunction(ch, p.FunctionArray, null, buffer.GetValue(ch.Index));
                if (active >= 0 && p.Functions[active].Class.Kind is AttributeKind.Shutter or AttributeKind.Strobe) role = AttributeKind.Shutter;
            }
            bool wrote = role switch
            {
                AttributeKind.Dimmer => SetBrightest(buffer, ch, functions),
                AttributeKind.Shutter => SetOpenShutter(buffer, ch, functions),
                AttributeKind.ColorAdd or AttributeKind.ColorRgb or AttributeKind.HsbBrightness or AttributeKind.HsbSaturation
                    or AttributeKind.CieBrightness => SetColor(buffer, functions),
                AttributeKind.Zoom => SetZoom(buffer, functions, zoom),
                _ => false,
            };
            if (wrote) written.Add(ch.Index);
        }
        return written.ToArray();
    }

    /// <summary>
    /// Colours the fixture as close to <paramref name="target"/>'s chromaticity as its colour system allows, keeping the
    /// rest of <paramref name="buffer"/> (call <see cref="OpenWhite"/> first). Every way the fixture can make colour is
    /// tried — additive emitters (levels solved for the target in CIE XYZ), CMY or RGB/CMY filters, indirect RGB, HSB,
    /// every slot of its colour wheels — each result is evaluated, and the one whose light (all beams together, weighted
    /// by their flux) comes closest to the target's chromaticity (CIE 1931 xy) while still letting light through wins.
    /// Returns the xy distance reached (0 = exact), or NaN when the fixture has no beam, is dark, or has no colour system
    /// (then the buffer is unchanged).
    /// </summary>
    public static float SetColor(FixtureDmxBuffer buffer, LightColor target)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var p = buffer.Profile;
        if (p.Beams.Count == 0) return float.NaN;
        var want = target.ToXy();
        var start = new FixtureDmxBuffer(p);
        start.CopyFrom(buffer);
        float bestScore = float.PositiveInfinity, bestDistance = float.NaN;
        FixtureDmxBuffer? best = null;
        var trial = new FixtureDmxBuffer(p);
        var scratch = new FixtureState(p);
        FixtureEvaluator.Evaluate(scratch, start, 0, null, applyDynamics: false);
        float startFlux = Mix(scratch).Flux;
        if (!(startFlux > 0f)) return float.NaN; // dark: nothing to colour

        void Consider()
        {
            FixtureEvaluator.Evaluate(scratch, trial, 0, null, applyDynamics: false);
            var (xy, flux) = Mix(scratch);
            float transmission = flux / startFlux;
            if (!(transmission > 0.005f) || !float.IsFinite(xy.X) || !float.IsFinite(xy.Y)) return;
            float distance = Vector2.Distance(xy, want);
            // a little light lost is worth a better colour; a much darker result has to be clearly closer
            float score = distance + 0.01f * (1f - MathF.Min(transmission, 1f));
            if (!(score < bestScore)) return;
            bestScore = score;
            bestDistance = distance;
            best ??= new FixtureDmxBuffer(p);
            best.CopyFrom(trial);
        }

        var targetRgb = Normalized(target.Rgb);
        if (Additive(start, trial, target)) Consider();
        if (Filters(start, trial, targetRgb)) Consider();
        if (IndirectRgb(start, trial, targetRgb)) Consider();
        if (Hsb(start, trial, target)) Consider();
        foreach (var f in p.Functions)
        {
            if (f.Class.Kind != AttributeKind.WheelSelect || f.Class.Wheel != WheelFamily.Color || f.Wheel < 0) continue;
            int slots = p.Wheels[f.Wheel].SlotCount;
            for (int slot = 1; slot <= slots; slot++)
            {
                trial.CopyFrom(start);
                DmxEncoder.EncodeFunction(trial, f.Index, slot);
                Consider();
            }
        }
        if (best is null) return float.NaN;
        buffer.CopyFrom(best);
        return bestDistance;
    }

    /// <summary>
    /// The colour (CIE 1931 xy) and luminous flux (lm) of all the fixture's beams together — what the fixture looks
    /// like, whichever beam the colour channels reach (an unaddressed aura or effect ring stays as it is).
    /// </summary>
    private static (Vector2 Xy, float Flux) Mix(FixtureState state)
    {
        var xyz = Vector3.Zero;
        float flux = 0f;
        foreach (var b in state.Beams)
        {
            if (!(b.Flux > 0f) || !float.IsFinite(b.Flux)) continue;
            var c = b.Color.ToXyz();
            if (!(c.Y > 0f)) continue;
            xyz += c * (b.Flux / c.Y);
            flux += b.Flux;
        }
        float sum = xyz.X + xyz.Y + xyz.Z;
        return sum > 0f ? (new Vector2(xyz.X / sum, xyz.Y / sum), flux) : (new Vector2(float.NaN), 0f);
    }

    private static Vector3 Normalized(Vector3 rgb)
    {
        rgb = Vector3.Max(rgb, Vector3.Zero);
        float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        return max > 0f ? rgb / max : Vector3.One;
    }

    /// <summary>Additive emitters: non-negative levels whose XYZ mix has the target's chromaticity, the largest at full.</summary>
    private static bool Additive(FixtureDmxBuffer start, FixtureDmxBuffer trial, LightColor target)
    {
        var p = start.Profile;
        var emitters = p.Emitters;
        if (emitters.Count == 0) return false;
        bool any = false;
        foreach (var f in p.Functions) any |= f.Class.Kind == AttributeKind.ColorAdd && f.Emitter >= 0;
        if (!any) return false;
        var xyz = new Vector3[emitters.Count];
        for (int e = 0; e < xyz.Length; e++)
        {
            var c = emitters[e].Chromaticity;
            float y = MathF.Max(c.Y, 1e-4f);
            xyz[e] = new Vector3(c.X / y, 1f, MathF.Max(1f - c.X - c.Y, 0f) / y) * MathF.Max(emitters[e].Weight, 1e-4f);
        }
        var goal = target.ToXyz();
        // non-negative least squares by projected coordinate descent (a handful of emitters)
        var levels = new float[xyz.Length];
        for (int iteration = 0; iteration < 400; iteration++)
        {
            for (int e = 0; e < levels.Length; e++)
            {
                var residual = goal;
                for (int k = 0; k < levels.Length; k++)
                    if (k != e) residual -= xyz[k] * levels[k];
                float norm = Vector3.Dot(xyz[e], xyz[e]);
                levels[e] = norm > 0f ? MathF.Max(0f, Vector3.Dot(residual, xyz[e]) / norm) : 0f;
            }
        }
        float max = levels.Max();
        if (!(max > 0f)) return false;
        trial.CopyFrom(start);
        foreach (var f in p.Functions)
        {
            if (f.Class.Kind != AttributeKind.ColorAdd || f.Emitter < 0) continue;
            DmxEncoder.EncodeFunction(trial, f.Index, f.MinValue + levels[f.Emitter] / max * (f.MaxValue - f.MinValue));
        }
        return true;
    }

    /// <summary>CMY (and RGB-named subtractive) filters: each filter removes its complement in proportion.</summary>
    private static bool Filters(FixtureDmxBuffer start, FixtureDmxBuffer trial, Vector3 rgb)
    {
        var p = start.Profile;
        bool any = false;
        trial.CopyFrom(start);
        foreach (var f in p.Functions)
        {
            if (f.Class.Kind != AttributeKind.ColorSub) continue;
            float level = f.ColorCode switch
            {
                3 => 1f - rgb.X,
                4 => 1f - rgb.Y,
                5 => 1f - rgb.Z,
                0 => 1f - MathF.Max(rgb.Y, rgb.Z), // a red filter passes red only
                1 => 1f - MathF.Max(rgb.X, rgb.Z),
                2 => 1f - MathF.Max(rgb.X, rgb.Y),
                _ => 0f,
            };
            DmxEncoder.EncodeFunction(trial, f.Index, f.MinValue + Math.Clamp(level, 0f, 1f) * (f.MaxValue - f.MinValue));
            any = true;
        }
        return any;
    }

    private static bool IndirectRgb(FixtureDmxBuffer start, FixtureDmxBuffer trial, Vector3 rgb)
    {
        var p = start.Profile;
        bool any = false;
        trial.CopyFrom(start);
        foreach (var f in p.Functions)
        {
            if (f.Class.Kind != AttributeKind.ColorRgb) continue;
            float level = f.ColorCode switch
            {
                0 => rgb.X,
                1 => rgb.Y,
                2 => rgb.Z,
                3 => 1f - rgb.X,
                4 => 1f - rgb.Y,
                5 => 1f - rgb.Z,
                _ => 1f,
            };
            DmxEncoder.EncodeFunction(trial, f.Index, f.MinValue + Math.Clamp(level, 0f, 1f) * (f.MaxValue - f.MinValue));
            any = true;
        }
        return any;
    }

    private static bool Hsb(FixtureDmxBuffer start, FixtureDmxBuffer trial, LightColor target)
    {
        var p = start.Profile;
        bool any = false;
        var srgb = Vector3.Max(target.ToRec709(), Vector3.Zero);
        float max = MathF.Max(srgb.X, MathF.Max(srgb.Y, srgb.Z)), min = MathF.Min(srgb.X, MathF.Min(srgb.Y, srgb.Z));
        float saturation = max > 0f ? (max - min) / max : 0f;
        float hue = 0f;
        if (max > min)
        {
            float d = max - min;
            hue = max == srgb.X ? 60f * (((srgb.Y - srgb.Z) / d) % 6f) : max == srgb.Y ? 60f * ((srgb.Z - srgb.X) / d + 2f) : 60f * ((srgb.X - srgb.Y) / d + 4f);
            if (hue < 0f) hue += 360f;
        }
        trial.CopyFrom(start);
        foreach (var f in p.Functions)
        {
            float t = f.Class.Kind switch
            {
                AttributeKind.HsbHue => hue / 360f,
                AttributeKind.HsbSaturation => saturation,
                AttributeKind.HsbBrightness => 1f,
                _ => float.NaN,
            };
            if (float.IsNaN(t)) continue;
            DmxEncoder.EncodeFunction(trial, f.Index, f.MinValue + Math.Clamp(t, 0f, 1f) * (f.MaxValue - f.MinValue));
            any = true;
        }
        return any;
    }

    /// <summary>What the channel is for in this look: its logical attribute, else the kind its functions share.</summary>
    private static AttributeKind Role(ProfileChannel ch, ReadOnlySpan<ProfileFunction> functions)
    {
        var kind = ch.Class.Kind;
        if (kind is AttributeKind.Strobe or AttributeKind.StrobeRate or AttributeKind.StrobeMode) return AttributeKind.Shutter;
        if (kind is AttributeKind.Dimmer or AttributeKind.Shutter or AttributeKind.ColorAdd or AttributeKind.ColorRgb or AttributeKind.Zoom
            or AttributeKind.HsbBrightness or AttributeKind.HsbSaturation or AttributeKind.CieBrightness)
            return kind;
        // a channel whose logical attribute is unclassified but whose functions open/close the light (e.g. "Shutter1Strobe"
        // as the logical name with Shutter functions) still needs opening; dimmer functions on control channels do not
        if (kind != AttributeKind.Ignored) return kind;
        foreach (var f in functions)
            if (f.Class.Kind is AttributeKind.Shutter or AttributeKind.Strobe) return AttributeKind.Shutter;
        return kind;
    }

    /// <summary>The dimmer function and value with the highest level that the channel's DMX actually selects.</summary>
    private static bool SetBrightest(FixtureDmxBuffer buffer, ProfileChannel ch, ReadOnlySpan<ProfileFunction> functions)
    {
        float bestLevel = -1f;
        uint bestDmx = 0;
        int bestFunction = -1;
        foreach (var f in functions)
        {
            if (f.Class.Kind != AttributeKind.Dimmer) continue;
            foreach (uint dmx in Candidates(f))
            {
                if (!Selects(buffer.Profile, ch, f, dmx)) continue;
                float level = Level(f, dmx);
                if (level > bestLevel + 1e-6f)
                {
                    bestLevel = level;
                    bestDmx = dmx;
                    bestFunction = f.Index;
                }
            }
        }
        if (bestFunction < 0) return false;
        Write(buffer, buffer.Profile.Functions[bestFunction], bestDmx);
        return true;
    }

    /// <summary>A value that leaves the shutter open without strobing (the current one when it already does).</summary>
    private static bool SetOpenShutter(FixtureDmxBuffer buffer, ProfileChannel ch, ReadOnlySpan<ProfileFunction> functions)
    {
        var p = buffer.Profile;
        uint current = buffer.GetValue(ch.Index);
        int active = FixtureEvaluator.SelectFunction(ch, p.FunctionArray, null, current);
        float now = active >= 0 ? Openness(p.Functions[active], current) : 0f;
        if (now >= 1f) return false;

        float best = -1f;
        uint bestDmx = 0;
        int bestFunction = -1;
        foreach (var f in functions)
        {
            foreach (uint dmx in Candidates(f))
            {
                if (!Selects(p, ch, f, dmx)) continue;
                float open = Openness(f, dmx);
                // prefer a range named "Open" among equally open values
                if ((f.FlagsOf(f.SetIndexAt(dmx)) & ChannelSetFlags.Open) != 0) open += 0.001f;
                if (open > best + 1e-6f)
                {
                    best = open;
                    bestDmx = dmx;
                    bestFunction = f.Index;
                }
            }
        }
        if (bestFunction < 0 || best < 0.5f || best <= now + 1e-3f) return false;
        Write(buffer, p.Functions[bestFunction], bestDmx);
        return true;
    }

    private static bool SetColor(FixtureDmxBuffer buffer, ReadOnlySpan<ProfileFunction> functions)
    {
        foreach (var f in functions)
        {
            switch (f.Class.Kind)
            {
                case AttributeKind.ColorAdd or AttributeKind.HsbBrightness or AttributeKind.CieBrightness:
                    DmxEncoder.EncodeFunction(buffer, f.Index, f.MaxValue);
                    return true;
                case AttributeKind.HsbSaturation:
                    DmxEncoder.EncodeFunction(buffer, f.Index, f.MinValue);
                    return true;
                case AttributeKind.ColorRgb:
                    // R, G, B full; the complementary C, M, Y channels of the same colour space off
                    DmxEncoder.EncodeFunction(buffer, f.Index, f.ColorCode is >= 3 and <= 5 ? f.MinValue : f.MaxValue);
                    return true;
            }
        }
        return false;
    }

    private static bool SetZoom(FixtureDmxBuffer buffer, ReadOnlySpan<ProfileFunction> functions, float zoom)
    {
        foreach (var f in functions)
        {
            if (f.Class.Kind != AttributeKind.Zoom) continue;
            DmxEncoder.EncodeFunction(buffer, f.Index, f.MinValue + zoom * (f.MaxValue - f.MinValue));
            return true;
        }
        return false;
    }

    /// <summary>The DMX values worth trying in a function: its ends and the start and end of every channel set.</summary>
    private static IEnumerable<uint> Candidates(ProfileFunction f)
    {
        yield return f.DmxTo;
        yield return f.DmxFrom;
        foreach (var s in f.Sets)
        {
            yield return s.DmxFrom;
            yield return s.DmxTo;
        }
    }

    /// <summary>True when <paramref name="dmx"/> on the channel selects <paramref name="f"/> (mode masters aside; they are set on write).</summary>
    private static bool Selects(FixtureProfile p, ProfileChannel ch, ProfileFunction f, uint dmx)
    {
        if (dmx < f.DmxFrom || dmx > f.DmxTo) return false;
        if (f.ModeMaster >= 0) return true; // decided by its mode master, which Write sets
        return FixtureEvaluator.SelectFunction(ch, p.FunctionArray, null, dmx) == f.Index;
    }

    private static void Write(FixtureDmxBuffer buffer, ProfileFunction f, uint dmx)
    {
        buffer.SetValue(f.Channel, dmx);
        if (f.ModeMaster < 0) return;
        uint mv = buffer.GetValue(f.ModeMaster);
        if (mv < f.ModeFrom || mv > f.ModeTo) buffer.SetValue(f.ModeMaster, f.ModeFrom);
    }

    /// <summary>Intensity level 0 … 1 of a dimmer function at a DMX value (0 inside a "Closed" range).</summary>
    private static float Level(ProfileFunction f, uint dmx)
    {
        int set = f.SetIndexAt(dmx);
        if ((f.FlagsOf(set) & ChannelSetFlags.Closed) != 0) return 0f;
        float physical = FixtureEvaluator.PhysicalAt(f, dmx, set);
        return float.IsFinite(physical) ? Math.Clamp(physical / f.LevelScale, 0f, 1f) : 0f;
    }

    /// <summary>How open a shutter/strobe function leaves the light at a DMX value: 1 open and steady, 0 closed or strobing.</summary>
    private static float Openness(ProfileFunction f, uint dmx)
    {
        var flags = f.FlagsOf(f.SetIndexAt(dmx));
        if ((flags & ChannelSetFlags.Closed) != 0) return f.Class.Kind == AttributeKind.StrobeRate ? 1f : 0f;
        bool open = (flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) != 0;
        float physical = FixtureEvaluator.PhysicalAt(f, dmx, f.SetIndexAt(dmx));
        return f.Class.Kind switch
        {
            AttributeKind.Shutter => open || f.LevelScale > 1f ? 1f : float.IsFinite(physical) ? Math.Clamp(physical, 0f, 1f) : 0f,
            AttributeKind.Strobe => open ? 1f : 0f,
            AttributeKind.StrobeRate => open || physical <= 0.01f ? 1f : 0f,
            AttributeKind.StrobeMode => open || f.Waveform == Waveform.None ? 1f : 0f,
            AttributeKind.Dimmer => Level(f, dmx),
            AttributeKind.StrobeDuration => 1f,
            _ => 0.5f,
        };
    }
}
