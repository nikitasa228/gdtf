namespace Candela.Dmx;

/// <summary>
/// Physical attribute values → DMX: the inverse of <see cref="FixtureEvaluator"/>'s DMX → physical mapping. Used by the
/// properties panel's manual control, the built-in test console and tests. Values are control values: the GDTF
/// physical value of the function (degrees, Hz, K, 0 … 1 …), except for wheel slot selection where the value is the
/// 1-based slot position (fractional for split positions).
/// </summary>
public static class DmxEncoder
{
    /// <summary>
    /// DMX value (at the channel's resolution) inside <paramref name="function"/>'s range that produces
    /// <paramref name="value"/>; values outside the function's range are clamped to it.
    /// </summary>
    public static uint ToDmx(ProfileFunction function, float value)
    {
        ArgumentNullException.ThrowIfNull(function);
        var f = function;
        if (f.UsesSlotPosition) return SlotToDmx(f, value);
        if (!float.IsFinite(value)) return f.DmxFrom;

        // candidates: inside every channel set whose own physical range contains the value, and through the function's
        // mapping; the one whose evaluated value is closest wins (sets override the mapping where they have values)
        uint best = f.DmxFrom;
        float bestErr = float.MaxValue;
        foreach (var s in f.Sets)
        {
            if (!s.HasPhysical || !Between(value, s.PhysicalFrom, s.PhysicalTo)) continue;
            uint d = s.DmxFrom;
            if (s.DmxTo > s.DmxFrom && s.PhysicalTo != s.PhysicalFrom)
            {
                double t = (value - s.PhysicalFrom) / (double)(s.PhysicalTo - s.PhysicalFrom);
                d = (uint)Math.Round(s.DmxFrom + Math.Clamp(t, 0, 1) * (s.DmxTo - s.DmxFrom));
            }
            Consider(Refine(f, d, value));
            if (bestErr == 0f) return best;
        }

        if (f.PhysicalTo == f.PhysicalFrom || f.DmxTo == f.DmxFrom)
        {
            Consider(FirstPlainDmx(f));
            return best;
        }
        double u = Math.Clamp((value - f.PhysicalFrom) / (double)(f.PhysicalTo - f.PhysicalFrom), 0, 1);
        if (f.Curve is not null) u = InvertCurve(f, u);
        uint dmx = (uint)Math.Round(f.DmxFrom + u * (f.DmxTo - f.DmxFrom));
        Consider(Refine(f, Math.Clamp(dmx, f.DmxFrom, f.DmxTo), value));
        return best;

        void Consider(uint d)
        {
            float e = Error(f, d, value);
            if (e < bestErr)
            {
                bestErr = e;
                best = d;
            }
        }
    }

    /// <summary>
    /// The function of <paramref name="channel"/> with <paramref name="attribute"/> that can produce
    /// <paramref name="value"/>: one whose range contains the value (preferring functions whose mode master is satisfied by
    /// <paramref name="current"/>, then functions without mode master), else the nearest one; −1 when the channel has no
    /// function with that attribute.
    /// </summary>
    public static int FindFunction(FixtureProfile profile, int channel, string attribute, float value, FixtureDmxBuffer? current = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int best = -1;
        int bestScore = int.MinValue;
        float bestDistance = float.MaxValue;
        foreach (var f in profile.FunctionsOf(channel))
        {
            if (!string.Equals(f.Attribute, attribute, StringComparison.OrdinalIgnoreCase)) continue;
            float distance = value < f.MinValue ? f.MinValue - value : value > f.MaxValue ? value - f.MaxValue : 0f;
            if (!float.IsFinite(distance)) distance = float.MaxValue;
            int score = 0;
            if (distance == 0f) score += 4;
            if (f.ModeMaster < 0) score += 1;
            else if (current is not null)
            {
                uint mv = current.GetValue(f.ModeMaster);
                if (mv >= f.ModeFrom && mv <= f.ModeTo) score += 2;
            }
            if (score > bestScore || (score == bestScore && distance < bestDistance))
            {
                best = f.Index;
                bestScore = score;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// Writes <paramref name="value"/> for function <paramref name="function"/> into <paramref name="buffer"/>; when the
    /// function needs a mode master that is not in range, the master is set to the start of its range as well.
    /// </summary>
    public static void EncodeFunction(FixtureDmxBuffer buffer, int function, float value)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var f = buffer.Profile.Functions[function];
        buffer.SetValue(f.Channel, ToDmx(f, value));
        if (f.ModeMaster >= 0)
        {
            uint mv = buffer.GetValue(f.ModeMaster);
            if (mv < f.ModeFrom || mv > f.ModeTo) buffer.SetValue(f.ModeMaster, f.ModeFrom);
        }
    }

    /// <summary>
    /// Sets <paramref name="attribute"/> (optionally <c>"Attribute@Instance"</c>, or restricted with
    /// <paramref name="instance"/>) to <paramref name="value"/> on every matching channel instance. Returns the number of
    /// channels written (0 when the fixture has no such attribute).
    /// </summary>
    public static int Encode(FixtureDmxBuffer buffer, string attribute, float value, string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(attribute);
        (attribute, instance) = SplitInstance(attribute, instance);
        var p = buffer.Profile;
        int written = 0;
        foreach (int c in p.FindChannels(attribute))
        {
            if (instance is not null && !string.Equals(p.Channels[c].InstanceName, instance, StringComparison.OrdinalIgnoreCase)) continue;
            int fn = FindFunction(p, c, attribute, value, buffer);
            if (fn < 0) continue;
            EncodeFunction(buffer, fn, value);
            written++;
        }
        return written;
    }

    /// <summary>Encodes several attribute values in order (later values win on shared channels); returns the channels written.</summary>
    public static int Encode(FixtureDmxBuffer buffer, IEnumerable<KeyValuePair<string, float>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = 0;
        foreach (var (k, v) in values) n += Encode(buffer, k, v);
        return n;
    }

    /// <summary>Splits <c>"Attribute@Instance"</c>; an explicit <paramref name="instance"/> wins.</summary>
    internal static (string Attribute, string? Instance) SplitInstance(string key, string? instance)
    {
        int at = key.IndexOf('@');
        if (at <= 0) return (key, instance);
        return (key[..at], instance ?? key[(at + 1)..]);
    }

    private static bool Between(float v, float a, float b) => v >= MathF.Min(a, b) - 1e-6f && v <= MathF.Max(a, b) + 1e-6f;

    /// <summary>First DMX value of a constant function that is not inside a channel set with a different physical value.</summary>
    private static uint FirstPlainDmx(ProfileFunction f)
    {
        foreach (var s in f.Sets)
            if (s.HasPhysical && s.Contains(f.DmxFrom) && s.PhysicalFrom != f.PhysicalFrom && s.DmxTo < f.DmxTo) return s.DmxTo + 1;
        return f.DmxFrom;
    }

    /// <summary>Picks the DMX value among d−1, d, d+1 whose evaluated physical value is closest to the requested one.</summary>
    private static uint Refine(ProfileFunction f, uint d, float value)
    {
        uint best = d;
        float bestErr = Error(f, d, value);
        if (d > f.DmxFrom)
        {
            float e = Error(f, d - 1, value);
            if (e < bestErr) (best, bestErr) = (d - 1, e);
        }
        if (d < f.DmxTo)
        {
            float e = Error(f, d + 1, value);
            if (e < bestErr) best = d + 1;
        }
        return best;
    }

    private static float Error(ProfileFunction f, uint d, float value) =>
        MathF.Abs(FixtureEvaluator.PhysicalAt(f, d, FixtureEvaluator.SetIndexAt(f, d)) - value);

    /// <summary>Normalised DMX position whose curve output equals <paramref name="target"/> (bisection; curves are monotonic in practice).</summary>
    private static double InvertCurve(ProfileFunction f, double target)
    {
        var curve = f.Curve!;
        float Eval(double t)
        {
            float c = curve.Evaluate((float)(t * 100.0));
            return float.IsFinite(c) ? Math.Clamp(c > 1.5f ? c / 100f : c, 0f, 1f) : (float)t;
        }
        double lo = 0, hi = 1;
        bool increasing = Eval(1) >= Eval(0);
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (lo + hi);
            if ((Eval(mid) < target) == increasing) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    private static uint SlotToDmx(ProfileFunction f, float position)
    {
        var d = f.SlotDmx;
        var p = f.SlotPosition;
        var interp = f.SlotInterpolate;
        if (!float.IsFinite(position)) return d[0];
        for (int pass = 0; pass < 3; pass++)
        {
            // try the position as given, then one wheel turn up or down (positions wrap)
            float pos = position;
            if (pass > 0 && f.Wheel >= 0)
            {
                int slots = (int)MathF.Round(p.Max());
                if (slots < 1) break;
                pos = pass == 1 ? position - slots : position + slots;
            }
            for (int i = 0; i < d.Length; i++)
            {
                if (MathF.Abs(p[i] - pos) > 1e-4f) continue;
                int end = i;
                while (end + 1 < d.Length && MathF.Abs(p[end + 1] - pos) <= 1e-4f && !interp[end]) end++;
                return (uint)(((ulong)d[i] + d[end]) / 2);
            }
            for (int i = 0; i + 1 < d.Length; i++)
            {
                if (!interp[i] || !Between(pos, p[i], p[i + 1]) || p[i + 1] == p[i]) continue;
                double t = (pos - p[i]) / (double)(p[i + 1] - p[i]);
                return (uint)Math.Round(d[i] + t * (d[i + 1] - d[i]));
            }
        }
        // nearest anchor
        int nearest = 0;
        for (int i = 1; i < d.Length; i++)
            if (MathF.Abs(p[i] - position) < MathF.Abs(p[nearest] - position)) nearest = i;
        return d[nearest];
    }
}

/// <summary>
/// Manual attribute values compiled against a profile: attribute name (optionally <c>"Attribute@Instance"</c>) →
/// control value, resolved once to (channel, function, value) so that applying them every frame is cheap. Built from
/// <c>FixtureComponent.ManualAttributes</c> by the engine, or from any dictionary.
/// </summary>
public sealed class AttributeOverrides
{
    private readonly Entry[] _entries;

    private AttributeOverrides(FixtureProfile profile, Entry[] entries, IReadOnlyList<string> unresolved)
    {
        Profile = profile;
        _entries = entries;
        Unresolved = unresolved;
    }

    /// <summary>The profile the overrides were compiled for.</summary>
    public FixtureProfile Profile { get; }

    /// <summary>Number of channel values the overrides set.</summary>
    public int Count => _entries.Length;

    /// <summary>Attribute names that no channel of the profile carries.</summary>
    public IReadOnlyList<string> Unresolved { get; }

    /// <summary>Compiles manual values; unknown attributes are listed in <see cref="Unresolved"/>, non-finite values are skipped.</summary>
    public static AttributeOverrides Compile(FixtureProfile profile, IEnumerable<KeyValuePair<string, float>> values)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(values);
        var entries = new List<Entry>();
        var unresolved = new List<string>();
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || !float.IsFinite(value)) continue;
            var (attribute, instance) = DmxEncoder.SplitInstance(key, null);
            bool any = false;
            foreach (int c in profile.FindChannels(attribute))
            {
                if (instance is not null && !string.Equals(profile.Channels[c].InstanceName, instance, StringComparison.OrdinalIgnoreCase)) continue;
                int fn = DmxEncoder.FindFunction(profile, c, attribute, value);
                if (fn < 0) continue;
                entries.RemoveAll(e => e.Channel == c);
                entries.Add(Entry.Create(profile.FunctionArray[fn], value));
                any = true;
            }
            if (!any) unresolved.Add(key);
        }
        return new AttributeOverrides(profile, entries.ToArray(), unresolved);
    }

    internal void Apply(ChannelRuntime[] rt)
    {
        foreach (ref readonly var e in _entries.AsSpan())
        {
            ref var r = ref rt[e.Channel];
            r.Function = e.Function;
            r.Dmx = e.Dmx;
            r.SetIndex = e.SetIndex;
            r.SetFlags = e.SetFlags;
            r.Physical = e.Physical;
            r.Target = e.Value;
        }
    }

    /// <summary>One manual value, decoded once: the channel's function, DMX value, channel set and physical value.</summary>
    private readonly record struct Entry(int Channel, int Function, float Value, uint Dmx, int SetIndex, ChannelSetFlags SetFlags, float Physical)
    {
        public static Entry Create(ProfileFunction f, float value)
        {
            float v = Math.Clamp(value, f.MinValue, f.MaxValue);
            uint dmx = DmxEncoder.ToDmx(f, v);
            int set = f.SetIndexAt(dmx);
            float physical = f.UsesSlotPosition ? FixtureEvaluator.PhysicalAt(f, dmx, set) : v;
            return new Entry(f.Channel, f.Index, v, dmx, set, f.FlagsOf(set), physical);
        }
    }
}
