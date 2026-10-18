using System.Numerics;
using System.Runtime.CompilerServices;
using Candela.Gdtf;
using Candela.Photometry;
using GdtfBeamType = Candela.Gdtf.BeamType;
using GdtfEmitter = Candela.Gdtf.Emitter;
using PhotometryBeamType = Candela.Photometry.BeamType;
using PhotometryEmitter = Candela.Photometry.Emitter;

namespace Candela.Dmx;

public sealed partial class FixtureProfile
{
    /// <summary>Compiles one fixture type + mode. Every step is guarded so that one bad element only produces a warning.</summary>
    private sealed class Builder
    {
        private const float DegToRad = MathF.PI / 180f;
        private readonly Dictionary<ChainKey, int> _instanceIndex = new();
        private readonly Dictionary<DmxChannel, List<int>> _instancesOf = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Wheel, int> _wheelIndex = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _emitterIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<BeamGeometry, BeamSolidAngleTable> _solidAngleCache = new(ReferenceEqualityComparer.Instance);
        private int[] _beamFirst = Array.Empty<int>(); // per channel: first beam index affected
        private int[] _beamCount = Array.Empty<int>(); // per channel: number of beams affected (contiguous)
        private int[] _subtreeSize = Array.Empty<int>();
        private bool? _emitterYVaries;

        public Builder(FixtureType fixtureType, DmxMode mode, FixtureSpecs? specs)
        {
            FixtureType = fixtureType;
            Mode = mode;
            Specs = specs;
            Category = specs?.Category ?? FixtureCategories.Guess(string.IsNullOrEmpty(fixtureType.LongName) ? fixtureType.Name : fixtureType.LongName);
            Key = $"{fixtureType.SourceFileName ?? fixtureType.Name}#{mode.Name}";
        }

        public string Key { get; }
        public FixtureType FixtureType { get; }
        public DmxMode Mode { get; }
        public FixtureSpecs? Specs { get; }
        public FixtureCategory Category { get; }
        public FixtureGeometry Geometry { get; private set; } = null!;
        public DmxBreakLayout[] Breaks { get; private set; } = Array.Empty<DmxBreakLayout>();
        public List<ProfileChannel> Channels { get; } = new();
        public List<ProfileFunction> Functions { get; } = new();
        public List<ProfileRelation> Relations { get; } = new();
        public List<ProfileWheel> Wheels { get; } = new();
        public List<ProfileEmitter> Emitters { get; } = new();
        public List<ProfileBeam> Beams { get; } = new();
        public List<ProfileAxis> Axes { get; } = new();
        public bool IsAtmospheric { get; private set; }
        public List<string> Warnings { get; } = new();
        public int[] SharedBeamChannels { get; private set; } = Array.Empty<int>();
        public int[] OutputChannels { get; private set; } = Array.Empty<int>();
        public int[] ModifierChannels { get; private set; } = Array.Empty<int>();
        public int[][] FollowerRelations { get; private set; } = Array.Empty<int[]>();
        public bool[] IsRelationMaster { get; private set; } = Array.Empty<bool>();
        public Dictionary<string, int[]> ChannelsByAttribute { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ChannelsByName { get; } = new(StringComparer.Ordinal);

        public FixtureProfile Build()
        {
            try
            {
                Geometry = new FixtureGeometryBuilder(FixtureType).Build(Mode);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new FixtureProfileException($"Geometry of '{FixtureType}' mode '{Mode.Name}' cannot be built: {e.Message}", e);
            }
            ComputeSubtreeSizes();
            BuildBreaks();
            BuildChannels();
            ResolveModeMasters();
            BuildRelations();
            BuildBeams();
            BuildAxes();
            BuildOutputsAndModifiers();
            AssignMotion();
            BuildColorSpace();
            BuildLookups();
            return new FixtureProfile(this);
        }

        // ------------------------------------------------------------------ layout

        private void ComputeSubtreeSizes()
        {
            var parts = Geometry.Parts;
            _subtreeSize = new int[parts.Count];
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                _subtreeSize[i] += 1;
                int p = parts[i].ParentIndex;
                if (p >= 0) _subtreeSize[p] += _subtreeSize[i];
            }
        }

        private void BuildBreaks()
        {
            var list = new List<DmxBreakLayout>();
            foreach (var (brk, footprint) in Mode.BreakFootprints.OrderBy(kv => kv.Key))
                if (footprint > 0) list.Add(new DmxBreakLayout(brk, footprint));
            Breaks = list.ToArray();
        }

        private int BreakIndex(int dmxBreak)
        {
            for (int i = 0; i < Breaks.Length; i++)
                if (Breaks[i].Break == dmxBreak) return i;
            return -1;
        }

        private void BuildChannels()
        {
            var instances = Mode.ChannelInstances;
            for (int i = 0; i < instances.Count; i++)
            {
                var inst = instances[i];
                var ch = inst.Channel;
                string attribute = ch.LogicalChannels.Count > 0 ? ch.LogicalChannels[0].AttributeName ?? "" : "";
                int part = -1;
                try
                {
                    part = Geometry.FindPart(inst);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Warnings.Add($"{inst}: geometry lookup failed ({e.Message}).");
                }
                int breakIndex = inst.Offsets.Length == 0 ? -1 : BreakIndex(inst.Break);
                var pc = new ProfileChannel(Channels.Count, inst, breakIndex, part, AttributeClassifier.Classify(attribute))
                {
                    Attribute = attribute,
                    Snap = ch.LogicalChannels.Count > 0 ? ch.LogicalChannels[0].Snap : SnapMode.No,
                };
                pc.DefaultValue = Math.Min(ch.DefaultValue, pc.MaxValue);
                // A virtual dimmer cannot be raised from the console; a default of 0 would black the fixture out for good.
                if (pc.IsVirtual && pc.Class.Kind == AttributeKind.Dimmer && pc.DefaultValue == 0) pc.DefaultValue = pc.MaxValue;
                if (ch.Highlight is { } hl) pc.HighlightValue = SafeResolution(hl, pc.ByteCount);
                Channels.Add(pc);
                _instanceIndex.TryAdd(new ChainKey(ch, inst.References, inst.References.Count), pc.Index);
                if (!_instancesOf.TryGetValue(ch, out var list)) _instancesOf[ch] = list = new List<int>();
                list.Add(pc.Index);

                pc.FirstFunction = Functions.Count;
                foreach (var src in ch.Functions)
                {
                    try
                    {
                        Functions.Add(CompileFunction(pc, src));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Warnings.Add($"{src.FullName}: skipped ({e.Message}).");
                    }
                }
                pc.FunctionCount = Functions.Count - pc.FirstFunction;
                pc.InitialFunction = pc.FunctionCount == 0 ? -1 : pc.FirstFunction;
                for (int k = pc.FirstFunction; k < pc.FirstFunction + pc.FunctionCount; k++)
                {
                    if (ReferenceEquals(Functions[k].Source, ch.InitialFunction))
                    {
                        pc.InitialFunction = k;
                        break;
                    }
                }
            }
        }

        private static uint SafeResolution(DmxValue v, int bytes)
        {
            try
            {
                return v.ToResolution(bytes);
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }

        private ProfileFunction CompileFunction(ProfileChannel ch, ChannelFunction src)
        {
            string attribute = string.IsNullOrEmpty(src.AttributeName) ? "NoFeature" : src.AttributeName;
            var cls = AttributeClassifier.Classify(attribute);
            var f = new ProfileFunction(Functions.Count, ch.Index, src, attribute, cls)
            {
                Unit = src.Attribute?.PhysicalUnit ?? PhysicalUnit.None,
                Curve = src.DmxProfile is { Points.Count: > 0 } ? src.DmxProfile : null,
            };
            uint max = ch.MaxValue;
            f.DmxFrom = Math.Min(src.DmxFromValue, max);
            f.DmxTo = Math.Clamp(src.DmxToValue, f.DmxFrom, max);
            f.PhysicalFrom = Finite(src.PhysicalFrom, 0f);
            f.PhysicalTo = Finite(src.PhysicalTo, 1f);

            // channel sets
            var sets = new List<ProfileChannelSet>(src.ChannelSets.Count);
            foreach (var s in src.ChannelSets)
            {
                uint from = Math.Clamp(s.DmxFromValue, f.DmxFrom, f.DmxTo);
                uint to = Math.Clamp(s.DmxToValue, from, f.DmxTo);
                float pf = s.PhysicalFrom is { } a && float.IsFinite(a) ? a : float.NaN;
                float pt = s.PhysicalTo is { } b && float.IsFinite(b) ? b : pf;
                var set = new ProfileChannelSet(s, from, to, pf, pt, NameParsing.Flags(s.Name, cls.Kind));
                switch (cls.Kind)
                {
                    case AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc:
                        if (NameParsing.TryParseKelvin(s.Name, out float k)) set.Kelvin = k;
                        break;
                    case AttributeKind.ColorMacro:
                        if (NameParsing.TryParseKelvin(s.Name, out float mk)) set.Kelvin = mk;
                        if (NameParsing.TryParseColor(s.Name, out var xy)) set.Color = xy;
                        break;
                    case AttributeKind.StrobeMode or AttributeKind.StrobeRate or AttributeKind.Strobe:
                        set.Waveform = NameParsing.ParseWaveform(s.Name);
                        break;
                    case AttributeKind.DimmerCurve:
                        set.DimmerCurve = NameParsing.ParseDimmerCurve(s.Name);
                        break;
                }
                sets.Add(set);
            }
            sets.Sort((x, y) => x.DmxFrom.CompareTo(y.DmxFrom));
            f.Sets = sets;

            float maxAbs = MathF.Max(MathF.Abs(f.PhysicalFrom), MathF.Abs(f.PhysicalTo));
            foreach (var s in sets)
                if (s.HasPhysical) maxAbs = MathF.Max(maxAbs, MathF.Max(MathF.Abs(s.PhysicalFrom), MathF.Abs(s.PhysicalTo)));
            f.LevelScale = maxAbs > 1.5f ? (maxAbs <= 100.5f ? 100f : maxAbs) : 1f;

            if (src.Wheel is { } wheel) f.Wheel = WheelIndexOf(wheel);
            if (cls.Kind == AttributeKind.ColorAdd) f.Emitter = EmitterIndexOf(src, cls.Code ?? "");
            if (cls.Kind is AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc)
            {
                float lo = float.NaN, hi = float.NaN;
                if (IsKelvin(f.PhysicalFrom) && IsKelvin(f.PhysicalTo))
                {
                    f.KelvinFrom = f.PhysicalFrom;
                    f.KelvinTo = f.PhysicalTo;
                    (lo, hi) = (MathF.Min(f.PhysicalFrom, f.PhysicalTo), MathF.Max(f.PhysicalFrom, f.PhysicalTo));
                }
                else
                {
                    foreach (var set in sets)
                        if (!float.IsNaN(set.Kelvin))
                        {
                            lo = float.IsNaN(lo) ? set.Kelvin : MathF.Min(lo, set.Kelvin);
                            hi = float.IsNaN(hi) ? set.Kelvin : MathF.Max(hi, set.Kelvin);
                        }
                }
                f.NeutralKelvin = cls.Kind switch
                {
                    AttributeKind.Cto => hi,
                    AttributeKind.Ctb => lo,
                    _ => float.NaN,
                };
            }
            f.Waveform = cls.Waveform;
            if (f.Wheel >= 0 && cls.Kind is AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake)
                BuildSlotMap(f, Wheels[f.Wheel].SlotCount);

            if (f.UsesSlotPosition)
            {
                f.MinValue = f.SlotPosition.Min();
                f.MaxValue = f.SlotPosition.Max();
            }
            else
            {
                f.MinValue = MathF.Min(f.PhysicalFrom, f.PhysicalTo);
                f.MaxValue = MathF.Max(f.PhysicalFrom, f.PhysicalTo);
                foreach (var s in sets)
                {
                    if (!s.HasPhysical) continue;
                    f.MinValue = MathF.Min(f.MinValue, MathF.Min(s.PhysicalFrom, s.PhysicalTo));
                    f.MaxValue = MathF.Max(f.MaxValue, MathF.Max(s.PhysicalFrom, s.PhysicalTo));
                }
            }
            return f;
        }

        private static bool IsKelvin(float v) => v is >= 1000f and <= 25000f;

        private static float Finite(float v, float fallback) => float.IsFinite(v) ? v : fallback;

        /// <summary>
        /// Slot positions of a wheel selection function. A slot set whose physical values are offsets within ±0.5 (GDTF's
        /// way of describing continuous wheel indexing, e.g. "Blue" −0.5…+0.5) maps linearly across the slot; other slot
        /// sets hold the slot. Between sets the position is interpolated when the slots are adjacent (split colours) and
        /// held otherwise. Going from the last slot to the first continues past the end (position n + 1 is slot 1 again),
        /// so a wheel turning through "Open" keeps moving in one direction.
        /// </summary>
        private static void BuildSlotMap(ProfileFunction f, int slotCount)
        {
            var dmx = new List<uint>();
            var pos = new List<float>();
            var interp = new List<bool>();
            int prevK = 0;
            float offset = 0f;
            foreach (var s in f.Sets)
            {
                int k = s.WheelSlot;
                if (k <= 0) continue;
                if (prevK > 0 && slotCount > 1)
                {
                    if (prevK == slotCount && k == 1) offset += slotCount;
                    else if (prevK == 1 && k == slotCount) offset -= slotCount;
                }
                bool continuous = s.HasPhysical && MathF.Abs(s.PhysicalFrom) <= 0.5001f && MathF.Abs(s.PhysicalTo) <= 0.5001f &&
                    s.PhysicalFrom != s.PhysicalTo;
                float p0 = k + offset + (continuous ? s.PhysicalFrom : 0f);
                float p1 = k + offset + (continuous ? s.PhysicalTo : 0f);
                if (dmx.Count > 0)
                {
                    bool adjacent = MathF.Abs(p0 - pos[^1]) <= 1.0001f;
                    interp[^1] = adjacent && s.DmxFrom > dmx[^1];
                }
                dmx.Add(s.DmxFrom);
                pos.Add(p0);
                interp.Add(continuous);
                if (s.DmxTo > s.DmxFrom)
                {
                    dmx.Add(s.DmxTo);
                    pos.Add(p1);
                    interp.Add(false);
                }
                prevK = k;
            }
            if (dmx.Count == 0)
            {
                if (slotCount <= 1) return;
                // no slot sets: the function scrolls over the whole wheel
                dmx.Add(f.DmxFrom);
                pos.Add(1);
                interp.Add(true);
                dmx.Add(Math.Max(f.DmxTo, f.DmxFrom));
                pos.Add(slotCount);
                interp.Add(false);
            }
            f.SlotDmx = dmx.ToArray();
            f.SlotPosition = pos.ToArray();
            f.SlotInterpolate = interp.ToArray();
        }

        // ------------------------------------------------------------------ references

        private int ResolveInstance(DmxChannel target, IReadOnlyList<GeometryReference> chain)
        {
            for (int len = chain.Count; len >= 0; len--)
                if (_instanceIndex.TryGetValue(new ChainKey(target, chain, len), out int idx)) return idx;
            return _instancesOf.TryGetValue(target, out var list) && list.Count > 0 ? list[0] : -1;
        }

        private void ResolveModeMasters()
        {
            foreach (var f in Functions)
            {
                var src = f.Source;
                if (!src.HasModeMaster) continue;
                var ch = Channels[f.Channel];
                int master = src.ModeMasterChannel is { } mc ? ResolveInstance(mc, ch.Instance.References) : -1;
                if (master < 0)
                {
                    Warnings.Add($"{src.FullName}: mode master '{src.ModeMasterName}' not found; the function is treated as always available.");
                    continue;
                }
                var m = Channels[master];
                uint from = SafeResolution(src.ModeFrom, m.ByteCount), to = SafeResolution(src.ModeTo, m.ByteCount);
                if (to < from) (from, to) = (to, from);
                f.ModeMaster = master;
                f.ModeFrom = from;
                f.ModeTo = to;
            }
        }

        private void BuildRelations()
        {
            var follower = new List<int>[Channels.Count];
            IsRelationMaster = new bool[Channels.Count];
            foreach (var r in Mode.Relations)
            {
                if (r.Master is not { } masterChannel || r.Follower?.LogicalChannel?.Channel is not { } followerChannel)
                {
                    Warnings.Add($"Relation '{r.Name}' is incomplete (master '{r.MasterName}', follower '{r.FollowerName}'); ignored.");
                    continue;
                }
                if (!_instancesOf.TryGetValue(followerChannel, out var instances)) continue;
                foreach (int fi in instances)
                {
                    var fch = Channels[fi];
                    int fn = -1;
                    for (int k = fch.FirstFunction; k < fch.FirstFunction + fch.FunctionCount; k++)
                        if (ReferenceEquals(Functions[k].Source, r.Follower)) { fn = k; break; }
                    int mi = ResolveInstance(masterChannel, fch.Instance.References);
                    if (fn < 0 || mi < 0 || mi == fi) continue;
                    (follower[fi] ??= new List<int>()).Add(Relations.Count);
                    Relations.Add(new ProfileRelation(mi, fi, fn, r.Type));
                    if (r.Type == RelationType.Multiply) IsRelationMaster[mi] = true;
                }
            }
            FollowerRelations = new int[Channels.Count][];
            for (int i = 0; i < Channels.Count; i++) FollowerRelations[i] = follower[i]?.ToArray() ?? Array.Empty<int>();
        }

        // ------------------------------------------------------------------ wheels and emitters

        private int WheelIndexOf(Wheel wheel)
        {
            if (_wheelIndex.TryGetValue(wheel, out int i)) return i;
            int n = Math.Max(1, wheel.Slots.Count);
            var transmission = new Vector3[n];
            var facets = new int[n];
            var open = new bool[n];
            for (int s = 0; s < n; s++)
            {
                if (s >= wheel.Slots.Count)
                {
                    transmission[s] = Vector3.One;
                    open[s] = true;
                    continue;
                }
                var slot = wheel.Slots[s];
                var c = slot.Color;
                float y = float.IsFinite(c.BigY) && c.BigY > 0f ? Math.Min(c.BigY, 100f) / 100f : 1f;
                transmission[s] = float.IsFinite(c.X) && float.IsFinite(c.Y) && c.Y > 0f
                    ? Filters.PhysicalChannelTransmission(LightColor.FromXy(c.X, c.Y), y)
                    : Vector3.One;
                facets[s] = slot.Facets.Count > 0 ? slot.Facets.Count : NameParsing.ParseFacetCount(slot.Name);
                open[s] = slot.MediaPath is null && slot.MediaFileName is null && slot.Facets.Count == 0 && slot.AnimationSystem is null &&
                    (NameParsing.IsOpenSlotName(slot.Name) || (y >= 0.99f && MathF.Abs(c.X - 0.3127f) < 0.01f && MathF.Abs(c.Y - 0.329f) < 0.01f && facets[s] == 0));
                // an open slot is a hole: files often store the lamp's own colour there, which is not a filter
                if (open[s] && y >= 0.9f) transmission[s] = Vector3.One;
            }
            i = Wheels.Count;
            Wheels.Add(new ProfileWheel(i, wheel, transmission, facets, open));
            _wheelIndex[wheel] = i;
            return i;
        }

        private int EmitterIndexOf(ChannelFunction src, string code)
        {
            var gdtf = src.Emitter ?? FindEmitterByName(code);
            string key = gdtf is not null ? "gdtf:" + gdtf.Name : "code:" + code;
            if (_emitterIndex.TryGetValue(key, out int i)) return i;
            Vector2 xy;
            float weight;
            string name;
            if (gdtf is not null && TryEmitterChromaticity(gdtf, out xy))
            {
                name = gdtf.Name;
                weight = EmitterYVaries() && gdtf.Color is { } c && float.IsFinite(c.BigY) && c.BigY > 0f ? c.BigY : StandardWeight(code, xy);
            }
            else
            {
                (xy, weight, name) = StandardForCode(code);
            }
            i = Emitters.Count;
            Emitters.Add(new ProfileEmitter(i, key, name, xy, weight, gdtf));
            _emitterIndex[key] = i;
            return i;
        }

        /// <summary>True when the file's emitter colours carry real relative outputs (their Y differ), not a constant placeholder.</summary>
        private bool EmitterYVaries()
        {
            if (_emitterYVaries is { } known) return known;
            float? first = null;
            bool varies = false;
            foreach (var e in FixtureType.PhysicalDescriptions.Emitters)
            {
                if (e.Color is not { } c || !float.IsFinite(c.BigY)) continue;
                if (first is null) first = c.BigY;
                else if (MathF.Abs(c.BigY - first.Value) > 0.5f) varies = true;
            }
            _emitterYVaries = varies;
            return varies;
        }

        private static bool TryEmitterChromaticity(GdtfEmitter e, out Vector2 xy)
        {
            if (e.Color is { } c && float.IsFinite(c.X) && float.IsFinite(c.Y) && c.Y > 0f && c.X >= 0f && c.X + c.Y <= 1.0001f)
            {
                xy = new Vector2(c.X, c.Y);
                return true;
            }
            foreach (var m in e.Measurements)
            {
                if (m.Points.Count < 3) continue;
                var w = new float[m.Points.Count];
                var v = new float[m.Points.Count];
                for (int k = 0; k < w.Length; k++) (w[k], v[k]) = (m.Points[k].WaveLength, m.Points[k].Energy);
                if (Spectrum.TryFromSamples(w, v, 5f, out var spectrum) && spectrum is not null)
                {
                    xy = Cie1931.ChromaticityFromSpectrum(spectrum);
                    if (float.IsFinite(xy.X) && float.IsFinite(xy.Y) && xy.Y > 0f) return true;
                }
            }
            if (e.DominantWaveLength is { } nm && nm is > 360f and < 830f)
            {
                xy = Cie1931.SpectralLocus(nm);
                return true;
            }
            xy = default;
            return false;
        }

        private GdtfEmitter? FindEmitterByName(string code)
        {
            var emitters = FixtureType.PhysicalDescriptions.Emitters;
            if (emitters.Count == 0) return null;
            string[] words = code switch
            {
                "R" => ["red"],
                "G" => ["green"],
                "B" => ["blue"],
                "W" => ["white", "w"],
                "WW" => ["warm white", "warm", "ww"],
                "CW" => ["cool white", "cold white", "cool", "cold", "cw"],
                "RY" or "A" => ["amber"],
                "GY" or "L" => ["lime", "mint"],
                "C" => ["cyan"],
                "UV" => ["uv", "ultraviolet", "ultra violet"],
                "I" or "BM" => ["indigo", "royal blue", "violet"],
                "M" => ["magenta"],
                "Y" => ["yellow"],
                "O" => ["orange"],
                "DR" => ["deep red", "dark red"],
                _ => Array.Empty<string>(),
            };
            foreach (string word in words)
                foreach (var e in emitters)
                    if (string.Equals(e.Name.Trim(), word, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (string word in words)
            {
                if (word.Length < 3) continue;
                foreach (var e in emitters)
                    if (e.Name.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                        !(code == "W" && (e.Name.Contains("warm", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("cool", StringComparison.OrdinalIgnoreCase))) &&
                        !(code == "R" && e.Name.Contains("deep", StringComparison.OrdinalIgnoreCase)))
                        return e;
            }
            return null;
        }

        /// <summary>Typical chromaticity, relative flux and name of a GDTF ColorAdd code (standard LED primaries).</summary>
        private static (Vector2 Xy, float Weight, string Name) StandardForCode(string code)
        {
            string std = code switch
            {
                "RY" => "A",
                "GY" => "L",
                "BC" => "BG",
                "BM" => "I",
                _ => code,
            };
            if (StandardEmitters.TryGet(std, out var e)) return (e.Chromaticity, e.RelativeFlux, e.Name);
            return code switch
            {
                "GC" => (Cie1931.SpectralLocus(505f), 0.35f, "Green-cyan"),
                "M" => (new Vector2(0.38f, 0.17f), 0.10f, "Magenta"),
                "RM" => (new Vector2(0.45f, 0.24f), 0.18f, "Pink"),
                "Y" => (new Vector2(0.50f, 0.49f), 0.50f, "Yellow"),
                "O" => (Cie1931.SpectralLocus(610f), 0.35f, "Orange"),
                "DR" => (Cie1931.SpectralLocus(660f), 0.04f, "Deep red"),
                _ => (Cct.PlanckianXy(6500f), 0.80f, string.IsNullOrEmpty(code) ? "Emitter" : code),
            };
        }

        /// <summary>Relative flux of the standard emitter nearest to <paramref name="xy"/> (code first when it is a standard one).</summary>
        private static float StandardWeight(string code, Vector2 xy)
        {
            var (stdXy, w, _) = StandardForCode(code);
            if (Vector2.Distance(stdXy, xy) < 0.08f) return w;
            float best = float.MaxValue;
            float weight = w;
            foreach (var e in StandardEmitters.Catalog)
            {
                float d = Vector2.DistanceSquared(e.Chromaticity, xy);
                if (d < best)
                {
                    best = d;
                    weight = e.RelativeFlux;
                }
            }
            return weight;
        }

        // ------------------------------------------------------------------ beams

        private static bool IsBeamKind(AttributeKind kind) => kind switch
        {
            AttributeKind.Ignored or AttributeKind.Pan or AttributeKind.Tilt or AttributeKind.PanRotate or AttributeKind.TiltRotate
                or AttributeKind.Translation or AttributeKind.Rotation or AttributeKind.MotionSpeed or AttributeKind.Fog
                or AttributeKind.Haze or AttributeKind.Fan or AttributeKind.FanDirection or AttributeKind.Flame
                or AttributeKind.FlameHeight => false,
            _ => true,
        };

        private bool AffectsBeams(ProfileChannel ch)
        {
            for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                if (IsBeamKind(Functions[k].Class.Kind)) return true;
            return false;
        }

        private void BuildBeams()
        {
            var parts = Geometry.Parts;
            for (int p = 0; p < parts.Count; p++)
            {
                if (parts[p].Geometry is not BeamGeometry bg) continue;
                string name = parts[p].References.Count > 0
                    ? (ReferenceEquals(parts[p].InstancedBy, parts[p].References[^1]) ? parts[p].Name : $"{parts[p].References[^1].Name}/{parts[p].Name}")
                    : parts[p].Name;
                Beams.Add(new ProfileBeam(Beams.Count, p, bg, name));
            }

            // channel → contiguous beam range (beams are in part order, a subtree is a contiguous part range)
            _beamFirst = new int[Channels.Count];
            _beamCount = new int[Channels.Count];
            var beamParts = Beams.Select(b => b.PartIndex).ToArray();
            foreach (var ch in Channels)
            {
                if (Beams.Count == 0 || !AffectsBeams(ch)) continue;
                int part = ch.PartIndex;
                if (part < 0)
                {
                    _beamFirst[ch.Index] = 0;
                    _beamCount[ch.Index] = Beams.Count;
                    continue;
                }
                while (part >= 0)
                {
                    int lo = LowerBound(beamParts, part), hi = LowerBound(beamParts, part + _subtreeSize[part]);
                    if (hi > lo)
                    {
                        _beamFirst[ch.Index] = lo;
                        _beamCount[ch.Index] = hi - lo;
                        break;
                    }
                    part = parts[part].ParentIndex;
                }
            }

            // relation masters already applied through their followers are excluded from those beams' dimmer product
            var excluded = new List<int>?[Beams.Count];
            foreach (var r in Relations)
            {
                if (r.Type != RelationType.Multiply) continue;
                int f0 = _beamFirst[r.Follower], fc = _beamCount[r.Follower];
                for (int b = f0; b < f0 + fc; b++)
                {
                    var list = excluded[b] ??= new List<int>();
                    if (!list.Contains(r.Master)) list.Add(r.Master);
                }
            }

            var own = new List<int>?[Beams.Count];
            var shared = new List<int>();
            foreach (var ch in Channels)
            {
                int first = _beamFirst[ch.Index], count = _beamCount[ch.Index];
                if (count == 0) continue;
                ch.Beams = Enumerable.Range(first, count).ToArray();
                if (count == Beams.Count && !IsRelationMaster[ch.Index])
                {
                    shared.Add(ch.Index);
                    continue;
                }
                for (int b = first; b < first + count; b++)
                {
                    if (IsRelationMaster[ch.Index] && excluded[b] is { } ex && ex.Contains(ch.Index)) continue;
                    (own[b] ??= new List<int>()).Add(ch.Index);
                }
            }
            SharedBeamChannels = shared.ToArray();
            for (int b = 0; b < Beams.Count; b++)
            {
                Beams[b].OwnChannels = own[b]?.ToArray() ?? Array.Empty<int>();
                Beams[b].ExcludedDimmers = excluded[b]?.ToArray() ?? Array.Empty<int>();
            }

            foreach (var beam in Beams)
            {
                try
                {
                    SetupBeamPhotometry(beam);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Warnings.Add($"Beam '{beam.Name}': photometry fallback used ({e.Message}).");
                    beam.BeamType = PhotometryBeamType.Wash;
                    beam.NativeBeamAngle = 25f * DegToRad;
                    beam.FieldRatio = 1.82f;
                    beam.SolidAngles = new BeamSolidAngleTable(PhotometryBeamType.Wash, beam.NativeBeamAngle, beam.NativeBeamAngle, beam.FieldRatio);
                    beam.ReferenceFlux = 1000f;
                    beam.PhotometrySource = PhotometrySource.Estimated;
                    beam.SourceKelvin = 6000f;
                }
            }
        }

        private static int LowerBound(int[] sorted, int value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private IEnumerable<int> ChannelsOfBeam(ProfileBeam beam)
        {
            foreach (int c in SharedBeamChannels) yield return c;
            foreach (int c in beam.OwnChannels) yield return c;
            foreach (int c in beam.ExcludedDimmers) yield return c;
        }

        private void SetupBeamPhotometry(ProfileBeam beam)
        {
            var bg = beam.Geometry;
            beam.BeamType = MapBeamType(bg.BeamType);

            // optics: native angle, field ratio, zoom range
            float native = bg.BeamAngle;
            float field = bg.FieldAngle;
            bool gdtfDefaults = native == 25f && field == 25f;
            if ((gdtfDefaults || !(native > 0f)) && Specs?.BeamAngleDeg is { } specBeam && specBeam.Min > 0)
            {
                native = (float)specBeam.Min;
                field = Specs.FieldAngleDeg is { } sf && sf > native ? (float)sf : float.NaN;
            }
            if (!(native > 0f) || !float.IsFinite(native)) native = 25f;
            float ratio;
            if (float.IsFinite(field) && field > native * 1.01f) ratio = field / native;
            else
            {
                var est = BeamDistribution.Create(beam.BeamType, native * DegToRad, float.NaN);
                ratio = est.BeamAngle > 0f ? est.FieldAngle / est.BeamAngle : 1.3f;
            }
            beam.NativeBeamAngle = native * DegToRad;
            beam.FieldRatio = Math.Clamp(ratio, 1f, 8f);

            float zoomMin = float.PositiveInfinity, zoomMax = 0f;
            bool hasCmyOrCto = false;
            var emitterSet = new SortedSet<int>();
            foreach (int c in ChannelsOfBeam(beam))
            {
                var ch = Channels[c];
                for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                {
                    var f = Functions[k];
                    switch (f.Class.Kind)
                    {
                        case AttributeKind.Zoom:
                            foreach (float a in (ReadOnlySpan<float>)[f.PhysicalFrom, f.PhysicalTo])
                            {
                                float d = MathF.Abs(a);
                                if (d is > 0.1f and < 180f)
                                {
                                    zoomMin = MathF.Min(zoomMin, d);
                                    zoomMax = MathF.Max(zoomMax, d);
                                }
                            }
                            break;
                        case AttributeKind.ColorSub or AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc:
                            hasCmyOrCto = true;
                            break;
                        case AttributeKind.ColorAdd when f.Emitter >= 0:
                            emitterSet.Add(f.Emitter);
                            break;
                    }
                }
            }
            bool hasZoom = zoomMax > 0f;
            float minAngle = hasZoom ? MathF.Min(zoomMin, native) : native;
            float maxAngle = hasZoom ? MathF.Max(zoomMax, native) : native;
            if (!_solidAngleCache.TryGetValue(bg, out var table) || table.MinBeamAngle > minAngle * DegToRad * 1.001f ||
                table.MaxBeamAngle < maxAngle * DegToRad * 0.999f || table.Type != beam.BeamType)
            {
                table = new BeamSolidAngleTable(beam.BeamType, minAngle * DegToRad, maxAngle * DegToRad, beam.FieldRatio);
                _solidAngleCache[bg] = table;
            }
            beam.SolidAngles = table;

            // reference output at full white
            if (Specs?.MaxLuminousIntensityCd is { } cd && cd > 0 && double.IsFinite(cd))
            {
                float perBeam = (float)(cd / Math.Max(1, Beams.Count));
                float specAngle = hasZoom ? zoomMin : native; // datasheet candela is the narrowest-zoom value
                beam.ReferenceFlux = perBeam * table.SolidAngle(specAngle * DegToRad);
                beam.PhotometrySource = PhotometrySource.SpecsCandela;
            }
            else if (bg.LuminousFlux > 0f && float.IsFinite(bg.LuminousFlux))
            {
                beam.ReferenceFlux = bg.LuminousFlux;
                beam.PhotometrySource = PhotometrySource.GdtfFlux;
            }
            else
            {
                float efficacy = bg.LampType switch
                {
                    LampType.Led => 40f,
                    LampType.Discharge => 45f,
                    LampType.Halogen => 18f,
                    _ => 15f,
                };
                float power = bg.PowerConsumption > 0f && float.IsFinite(bg.PowerConsumption) ? bg.PowerConsumption : 100f;
                beam.ReferenceFlux = power * efficacy;
                beam.PhotometrySource = PhotometrySource.Estimated;
            }

            // white source colour
            float kelvin = bg.ColorTemperature;
            if (bg.EmitterSpectrum is { } spectrumEmitter && TryEmitterChromaticity(spectrumEmitter, out var sxy))
            {
                var cct = Cct.FromXy(sxy);
                if (cct.IsMeaningful) kelvin = cct.Kelvin;
            }
            if (!(kelvin > 0f) || !float.IsFinite(kelvin)) kelvin = 6000f;
            beam.SourceKelvin = Math.Clamp(kelvin, 1000f, 25000f);
            beam.SourceXyz = ColorSpaces.XyToXyz(Cct.XyFromCct(beam.SourceKelvin));
            if (hasCmyOrCto) beam.Subtractive = SubtractiveColorTable.ForKelvin(beam.SourceKelvin);

            // emitters with their share of the reference flux
            if (emitterSet.Count > 0)
            {
                float wsum = 0f;
                foreach (int e in emitterSet) wsum += MathF.Max(Emitters[e].Weight, 1e-6f);
                beam.EmitterSlot = new int[Emitters.Count];
                Array.Fill(beam.EmitterSlot, -1);
                var list = new PhotometryEmitter[emitterSet.Count];
                int i = 0;
                foreach (int e in emitterSet)
                {
                    var pe = Emitters[e];
                    list[i] = new PhotometryEmitter(pe.Name, pe.Chromaticity, beam.ReferenceFlux * MathF.Max(pe.Weight, 1e-6f) / wsum);
                    beam.EmitterSlot[e] = i++;
                }
                beam.Emitters = list;
            }
        }

        private static PhotometryBeamType MapBeamType(GdtfBeamType type) => type switch
        {
            GdtfBeamType.Spot => PhotometryBeamType.Spot,
            GdtfBeamType.None => PhotometryBeamType.None,
            GdtfBeamType.Rectangle => PhotometryBeamType.Rectangle,
            GdtfBeamType.PC => PhotometryBeamType.PC,
            GdtfBeamType.Fresnel => PhotometryBeamType.Fresnel,
            GdtfBeamType.Glow => PhotometryBeamType.Glow,
            _ => PhotometryBeamType.Wash,
        };

        // ------------------------------------------------------------------ axes, outputs, modifiers

        private void BuildAxes()
        {
            var parts = Geometry.Parts;
            var axisOfPart = new Dictionary<int, int>();
            var axisChannels = new List<List<int>>();

            int AxisFor(int part)
            {
                if (!axisOfPart.TryGetValue(part, out int a))
                {
                    a = Axes.Count;
                    Axes.Add(new ProfileAxis(a, part, parts[part].Name));
                    axisChannels.Add(new List<int>());
                    axisOfPart[part] = a;
                }
                return a;
            }

            foreach (var ch in Channels)
            {
                var kind = ch.Class.Kind;
                if (kind is not (AttributeKind.Pan or AttributeKind.Tilt or AttributeKind.PanRotate or AttributeKind.TiltRotate
                    or AttributeKind.Rotation or AttributeKind.Translation))
                {
                    // a channel whose first function is e.g. "NoFeature" may still carry Pan/Tilt functions
                    kind = AttributeKind.Ignored;
                    for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                    {
                        var fk = Functions[k].Class.Kind;
                        if (fk is AttributeKind.Pan or AttributeKind.Tilt or AttributeKind.PanRotate or AttributeKind.TiltRotate
                            or AttributeKind.Rotation or AttributeKind.Translation)
                        {
                            kind = fk;
                            break;
                        }
                    }
                    if (kind == AttributeKind.Ignored) continue;
                }
                if (parts.Count == 0) continue;
                int part = ch.PartIndex >= 0 ? ch.PartIndex : 0;
                int target = part;
                if (parts[part].Type != GeometryType.Axis)
                {
                    int end = part + _subtreeSize[part];
                    int firstAxis = -1, secondAxis = -1;
                    for (int p = part; p < end; p++)
                    {
                        if (parts[p].Type != GeometryType.Axis) continue;
                        if (firstAxis < 0) firstAxis = p;
                        else if (secondAxis < 0 && IsAncestor(firstAxis, p)) secondAxis = p;
                    }
                    bool tiltLike = kind is AttributeKind.Tilt or AttributeKind.TiltRotate;
                    target = tiltLike && secondAxis >= 0 ? secondAxis : firstAxis >= 0 ? firstAxis : part;
                }
                int axis = AxisFor(target);
                ch.Axis = axis;
                axisChannels[axis].Add(ch.Index);
            }
            for (int a = 0; a < Axes.Count; a++) Axes[a].Channels = axisChannels[a].ToArray();
        }

        private bool IsAncestor(int ancestor, int part)
        {
            var parts = Geometry.Parts;
            for (int p = parts[part].ParentIndex; p >= 0; p = parts[p].ParentIndex)
                if (p == ancestor) return true;
            return false;
        }

        private void BuildOutputsAndModifiers()
        {
            bool hasOutputs = false;
            foreach (var f in Functions)
                if (f.Class.Kind is AttributeKind.Fog or AttributeKind.Haze or AttributeKind.Flame) hasOutputs = true;
            IsAtmospheric = Category is FixtureCategory.Haze or FixtureCategory.Fog or FixtureCategory.Fan or FixtureCategory.GeneratorFire ||
                (hasOutputs && Beams.Count == 0);

            var outputs = new List<int>();
            var modifiers = new List<int>();
            foreach (var ch in Channels)
            {
                bool output = false, modifier = false;
                for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                {
                    var f = Functions[k];
                    switch (f.Class.Kind)
                    {
                        case AttributeKind.Fog or AttributeKind.Haze or AttributeKind.Flame or AttributeKind.FlameHeight:
                            output = true;
                            break;
                        case AttributeKind.Fan or AttributeKind.FanDirection:
                            output |= IsAtmospheric;
                            break;
                        case AttributeKind.MotionSpeed or AttributeKind.DimmerCurve:
                            modifier = true;
                            break;
                    }
                    foreach (var s in f.Sets)
                        if ((s.Flags & (ChannelSetFlags.InvertPan | ChannelSetFlags.InvertTilt)) != 0) modifier = true;
                }
                if (output) outputs.Add(ch.Index);
                if (modifier) modifiers.Add(ch.Index);
            }

            // GENERATOR FIRE devices without Flame/Fire attributes: their dimmer/fog channel fires the flame
            if (Category == FixtureCategory.GeneratorFire && !Functions.Any(f => f.Class.Kind == AttributeKind.Flame))
                foreach (var ch in Channels)
                    if (ch.Class.Kind is AttributeKind.Dimmer or AttributeKind.Fog)
                    {
                        if (!outputs.Contains(ch.Index)) outputs.Add(ch.Index);
                        FlameChannel = ch.Index;
                        break;
                    }
            OutputChannels = outputs.ToArray();
            ModifierChannels = modifiers.ToArray();
        }

        public int FlameChannel { get; private set; } = -1;

        public Vector3[] ColorSpacePrimaries { get; private set; } = Array.Empty<Vector3>();

        private void BuildColorSpace()
        {
            static Vector3 Xyz(Candela.Core.SceneGraph.Components.CieColor c) =>
                c.Y > 0f && float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.BigY)
                    ? new Vector3(c.X / c.Y * c.BigY, c.BigY, (1f - c.X - c.Y) / c.Y * c.BigY)
                    : new Vector3(float.NaN);
            var cs = FixtureType.PhysicalDescriptions.ColorSpace;
            var r = Xyz(cs.Red);
            var g = Xyz(cs.Green);
            var b = Xyz(cs.Blue);
            var w = r + g + b;
            if (!float.IsFinite(w.X + w.Y + w.Z) || MathF.Abs(w.Y - 1f) > 0.05f || r.Y < 0f || g.Y < 0f || b.Y < 0f)
            {
                var srgb = ColorSpace.Create(ColorSpaceMode.Srgb);
                (r, g, b) = (Xyz(srgb.Red), Xyz(srgb.Green), Xyz(srgb.Blue));
                if (cs.Mode != ColorSpaceMode.Srgb) Warnings.Add($"Colour space '{cs.Name}' is degenerate; sRGB primaries used.");
            }
            ColorSpacePrimaries = [r, g, b];
        }

        private void AssignMotion()
        {
            bool ledSource = Beams.Count > 0 && Beams.All(b => b.Geometry.LampType == LampType.Led);
            foreach (var f in Functions)
            {
                int slots = f.Wheel >= 0 ? Wheels[f.Wheel].SlotCount : 0;
                (f.Motion, f.Limits) = MotorDynamics.DefaultMotion(f, Category, slots, mechanicalColor: !ledSource);
            }
        }

        private void BuildLookups()
        {
            var byAttr = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in Channels)
            {
                for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                {
                    string a = Functions[k].Attribute;
                    if (!byAttr.TryGetValue(a, out var list)) byAttr[a] = list = new List<int>();
                    if (list.Count == 0 || list[^1] != ch.Index) list.Add(ch.Index);
                }
                ChannelsByName.TryAdd(ch.Name, ch.Index);
                ChannelsByName.TryAdd($"{ch.Name}@{ch.InstanceName}", ch.Index);
            }
            foreach (var (k, v) in byAttr) ChannelsByAttribute[k] = v.ToArray();
        }
    }

    /// <summary>Key of a channel instance: the template channel plus a prefix of its geometry-reference chain (compared by identity).</summary>
    private readonly struct ChainKey : IEquatable<ChainKey>
    {
        private readonly DmxChannel _channel;
        private readonly IReadOnlyList<GeometryReference> _chain;
        private readonly int _length;
        private readonly int _hash;

        public ChainKey(DmxChannel channel, IReadOnlyList<GeometryReference> chain, int length)
        {
            _channel = channel;
            _chain = chain;
            _length = length;
            var h = new HashCode();
            h.Add(RuntimeHelpers.GetHashCode(channel));
            for (int i = 0; i < length; i++) h.Add(RuntimeHelpers.GetHashCode(chain[i]));
            _hash = h.ToHashCode();
        }

        public bool Equals(ChainKey other)
        {
            if (!ReferenceEquals(_channel, other._channel) || _length != other._length) return false;
            for (int i = 0; i < _length; i++)
                if (!ReferenceEquals(_chain[i], other._chain[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is ChainKey k && Equals(k);

        public override int GetHashCode() => _hash;
    }
}
