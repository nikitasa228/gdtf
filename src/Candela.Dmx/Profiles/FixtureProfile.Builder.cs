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

        /// <summary>GDTF's default Beam LuminousFlux (written by editors when the author left the field alone), lm.</summary>
        private const float GdtfDefaultFlux = 10000f;
        private readonly Dictionary<ChainKey, int> _instanceIndex = new();
        private readonly Dictionary<DmxChannel, List<int>> _instancesOf = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Wheel, int> _wheelIndex = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _emitterIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<BeamGeometry, BeamSolidAngleTable> _solidAngleCache = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, WhiteEfficiencyTable?> _whiteTables = new(StringComparer.Ordinal);
        private bool? _colouredEmitters;
        private int[] _beamFirst = Array.Empty<int>(); // per channel: first beam index affected
        private int[] _beamCount = Array.Empty<int>(); // per channel: number of beams affected (contiguous)
        private int[] _subtreeSize = Array.Empty<int>();
        private float[] _beamShare = Array.Empty<float>(); // per beam: share of the fixture's specs output
        private bool? _emitterYVaries;
        private bool? _cmyAsColorAdd;
        private readonly Dictionary<AttributeKind, bool> _realRange = new();
        private bool? _plainZoom;

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
            ResolveColorTemperatureSelectors();
            BuildAxes();
            BuildOutputsAndModifiers();
            AssignMotion();
            BuildColorSpace();
            BuildDecodeTables();
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

        /// <summary>
        /// True when the mode's only additive colour attributes are ColorAdd_C/M/Y: real files use them for CMY colour
        /// mixing (subtractive flags or a virtual CMY mode; DMX 0 = white), never for a cyan + magenta + yellow emitter set.
        /// </summary>
        private bool CmyWrittenAsColorAdd()
        {
            if (_cmyAsColorAdd is { } known) return known;
            bool any = false, onlyCmy = true;
            foreach (var ch in Mode.Channels)
                foreach (var lc in ch.LogicalChannels)
                    foreach (var f in lc.Functions)
                    {
                        if (AttributeClassifier.Classify(f.AttributeName) is not { Kind: AttributeKind.ColorAdd } c) continue;
                        any = true;
                        onlyCmy &= c.Code is "C" or "M" or "Y";
                    }
            _cmyAsColorAdd = any && onlyCmy;
            if (_cmyAsColorAdd.Value) Warnings.Add("ColorAdd_C/M/Y without other emitters are read as CMY colour mixing (ColorSub).");
            return _cmyAsColorAdd.Value;
        }

        /// <summary>Classifies an attribute of this mode (see <see cref="CmyWrittenAsColorAdd"/> for the one mode-dependent rule).</summary>
        private AttributeClass Classify(string attribute)
        {
            var cls = AttributeClassifier.Classify(attribute);
            return cls.Kind == AttributeKind.ColorAdd && CmyWrittenAsColorAdd() ? cls with { Kind = AttributeKind.ColorSub } : cls;
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
                var pc = new ProfileChannel(Channels.Count, inst, breakIndex, part, Classify(attribute))
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
            var cls = Classify(attribute);
            float physicalFrom = Finite(src.PhysicalFrom, 0f), physicalTo = Finite(src.PhysicalTo, 1f);
            // GDTF's default physical range 0 … 1 is a placeholder unless channel sets carry the real values
            bool defaultRange = IsDefaultRange(physicalFrom, physicalTo);
            bool setsReal = defaultRange && src.ChannelSets.Exists(s =>
                (s.PhysicalFrom is { } a && float.IsFinite(a) && MathF.Abs(a) > 1.5f) || (s.PhysicalTo is { } b && float.IsFinite(b) && MathF.Abs(b) > 1.5f));
            bool placeholder = defaultRange && !setsReal;
            float offset = 0f, scale = 1f;
            // ZoomModeSpot/ZoomModeBeam (zoom of a spot or beam mode, often a preset or mode switch) next to a Zoom channel:
            // the Zoom channel sets the beam angle, otherwise the last of the two channels would always win
            if (cls.Kind == AttributeKind.Zoom && !string.Equals(attribute, "Zoom", StringComparison.OrdinalIgnoreCase) && ModeHasPlainZoom())
            {
                Warnings.Add($"{src.FullName}: {attribute} next to a Zoom channel; the beam angle follows the Zoom channel.");
                cls = AttributeClass.Ignored;
            }
            if (placeholder && cls.Kind is AttributeKind.Zoom or AttributeKind.Pan or AttributeKind.Tilt)
                (cls, offset, scale) = ResolvePlaceholderRange(ch, src, cls);
            var f = new ProfileFunction(Functions.Count, ch.Index, src, attribute, cls)
            {
                Unit = src.Attribute?.PhysicalUnit ?? PhysicalUnit.None,
                Curve = src.DmxProfile is { Points.Count: > 0 } ? src.DmxProfile : null,
                DefaultPhysicalRange = placeholder,
            };
            uint max = ch.MaxValue;
            f.DmxFrom = Math.Min(src.DmxFromValue, max);
            f.DmxTo = Math.Clamp(src.DmxToValue, f.DmxFrom, max);
            f.PhysicalFrom = offset + scale * physicalFrom;
            f.PhysicalTo = offset + scale * physicalTo;

            // a shutter function named "Open" / "Closed" means that wherever no named channel set says otherwise
            var nameFlags = cls.Kind == AttributeKind.Shutter ? NameParsing.Flags(src.Name, cls.Kind) & (ChannelSetFlags.Open | ChannelSetFlags.Closed) : 0;
            // "Open/Close", "open / closed": a range that does both, not a state
            if (src.Name.Contains("open", StringComparison.OrdinalIgnoreCase) && src.Name.Contains("clos", StringComparison.OrdinalIgnoreCase))
                nameFlags = ChannelSetFlags.None;
            f.NameFlags = nameFlags;

            // channel sets
            var sets = new List<ProfileChannelSet>(src.ChannelSets.Count);
            foreach (var s in src.ChannelSets)
            {
                uint from = Math.Clamp(s.DmxFromValue, f.DmxFrom, f.DmxTo);
                uint to = Math.Clamp(s.DmxToValue, from, f.DmxTo);
                float pf = s.PhysicalFrom is { } a && float.IsFinite(a) ? offset + scale * a : float.NaN;
                float pt = s.PhysicalTo is { } b && float.IsFinite(b) ? offset + scale * b : pf;
                var setFlags = string.IsNullOrWhiteSpace(s.Name) ? nameFlags : NameParsing.Flags(s.Name, cls.Kind);
                var set = new ProfileChannelSet(s, from, to, pf, pt, setFlags);
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
            f.SetSets(sets.ToArray());

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
                // a default 0 … 1 function range whose channel sets carry the real values (degrees, Hz …): the sets define it
                f.MinValue = setsReal ? float.PositiveInfinity : MathF.Min(f.PhysicalFrom, f.PhysicalTo);
                f.MaxValue = setsReal ? float.NegativeInfinity : MathF.Max(f.PhysicalFrom, f.PhysicalTo);
                foreach (var s in sets)
                {
                    if (!s.HasPhysical) continue;
                    f.MinValue = MathF.Min(f.MinValue, MathF.Min(s.PhysicalFrom, s.PhysicalTo));
                    f.MaxValue = MathF.Max(f.MaxValue, MathF.Max(s.PhysicalFrom, s.PhysicalTo));
                }
                if (!(f.MinValue <= f.MaxValue))
                {
                    f.MinValue = MathF.Min(f.PhysicalFrom, f.PhysicalTo);
                    f.MaxValue = MathF.Max(f.PhysicalFrom, f.PhysicalTo);
                }
            }
            return f;
        }

        private static bool IsKelvin(float v) => v is >= 1000f and <= 25000f;

        /// <summary>True for GDTF's default physical range 0 … 1 (either direction).</summary>
        private static bool IsDefaultRange(float from, float to) => (from == 0f && to == 1f) || (from == 1f && to == 0f);

        /// <summary>
        /// A Zoom, Pan or Tilt function with GDTF's default physical range 0 … 1 — degrees are meant, the author left the
        /// field alone — is mapped onto the fixture's researched range (<c>_specs</c> beamAngleDeg, panRangeDeg,
        /// tiltRangeDeg; pan and tilt centred on 0°), so that it does not zoom to a 1° pencil or pan by 1°. When the
        /// function sits on a channel of another attribute, or the mode already has a real range for the attribute (a
        /// mislabelled effect such as a "zoom lens rotation"), or no range is known for a zoom, it does not drive the
        /// attribute at all. Returns the class to use and the linear map physical → physical′ = offset + scale·physical.
        /// </summary>
        private (AttributeClass Class, float Offset, float Scale) ResolvePlaceholderRange(ProfileChannel ch, ChannelFunction src, AttributeClass cls)
        {
            var kind = cls.Kind;
            if (ch.Class.Kind != kind || ModeHasRealRange(kind))
            {
                Warnings.Add($"{src.FullName}: {kind} with GDTF's placeholder range 0…1 next to a real {kind} range; not used as {kind}.");
                return (AttributeClass.Ignored, 0f, 1f);
            }
            if (kind == AttributeKind.Zoom)
            {
                if (Specs?.BeamAngleDeg is { } b && b.Min > 0 && b.Max > b.Min * 1.05 && b.Max < 180 && double.IsFinite(b.Max))
                {
                    Warnings.Add(FormattableString.Invariant($"{src.FullName}: Zoom with GDTF's placeholder range 0…1; mapped to {b.Min}…{b.Max}° (specs)."));
                    return (cls, (float)b.Min, (float)(b.Max - b.Min));
                }
                Warnings.Add($"{src.FullName}: Zoom with GDTF's placeholder range 0…1 and no known zoom range; the beam keeps its native angle.");
                return (AttributeClass.Ignored, 0f, 1f);
            }
            double? range = kind == AttributeKind.Pan ? Specs?.PanRangeDeg : Specs?.TiltRangeDeg;
            if (range is { } r && r > 0 && r <= 3600 && double.IsFinite(r))
            {
                Warnings.Add(FormattableString.Invariant($"{src.FullName}: {kind} with GDTF's placeholder range 0…1; mapped to ±{r / 2}° (specs)."));
                return (cls, (float)(-r / 2), (float)r);
            }
            return (cls, 0f, 1f);
        }

        /// <summary>True when a channel function of the mode has the attribute <c>Zoom</c> itself.</summary>
        private bool ModeHasPlainZoom()
        {
            if (_plainZoom is { } known) return known;
            bool any = false;
            foreach (var ch in Mode.Channels)
                foreach (var fn in ch.Functions)
                    any |= string.Equals(fn.AttributeName, "Zoom", StringComparison.OrdinalIgnoreCase);
            _plainZoom = any;
            return any;
        }

        /// <summary>True when a function of the mode with attribute kind <paramref name="kind"/> has a physical range other than GDTF's default 0 … 1.</summary>
        private bool ModeHasRealRange(AttributeKind kind)
        {
            if (_realRange.TryGetValue(kind, out bool known)) return known;
            bool real = false;
            foreach (var ch in Mode.Channels)
                foreach (var fn in ch.Functions)
                {
                    if (real || AttributeClassifier.Classify(fn.AttributeName ?? "").Kind != kind) continue;
                    float a = Finite(fn.PhysicalFrom, 0f), b = Finite(fn.PhysicalTo, 1f);
                    real = (!IsDefaultRange(a, b) && a != b) || fn.ChannelSets.Exists(s => s.PhysicalFrom is { } v && float.IsFinite(v) && MathF.Abs(v) > 1.5f);
                }
            _realRange[kind] = real;
            return real;
        }

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

        /// <summary>
        /// True when the file's emitter colours carry measured relative outputs: their Y values differ, and no two emitters of
        /// clearly different colour share the same Y (red and green both at 100 are placeholders, not measurements).
        /// </summary>
        private bool EmitterYVaries()
        {
            if (_emitterYVaries is { } known) return known;
            var points = new List<(Vector2 Xy, float Y)>();
            foreach (var e in FixtureType.PhysicalDescriptions.Emitters)
                if (e.Color is { } c && float.IsFinite(c.BigY) && c.BigY > 0f && TryEmitterChromaticity(e, out var xy)) points.Add((xy, c.BigY));
            bool varies = false, placeholder = false;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    float dy = MathF.Abs(points[i].Y - points[j].Y);
                    if (dy > 0.5f) varies = true;
                    else if (dy <= 1e-3f * MathF.Max(points[i].Y, points[j].Y) && Vector2.Distance(points[i].Xy, points[j].Xy) > 0.1f) placeholder = true;
                }
            }
            _emitterYVaries = varies && !placeholder;
            return _emitterYVaries.Value;
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
                foreach (int c in Beams[b].OwnChannels)
                    for (int k = Channels[c].FirstFunction; k < Channels[c].FirstFunction + Channels[c].FunctionCount; k++)
                        if (Functions[k].Class.Wheel != WheelFamily.None ||
                            Functions[k].Class.Kind is AttributeKind.BladeA or AttributeKind.BladeB or AttributeKind.BladeRotation or AttributeKind.BladeSoft)
                            Beams[b].OwnMechanics = true;
            }

            ComputeBeamShares();
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
            bool hasCmyOrCto = false, hasColorSub = false, hasColorTemperature = false, hasCtc = false, hasCrossfade = false;
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
                            foreach (float a in (ReadOnlySpan<float>)[f.MinValue, f.MaxValue])
                            {
                                float d = MathF.Abs(a);
                                if (d is > 0.1f and < 180f)
                                {
                                    zoomMin = MathF.Min(zoomMin, d);
                                    zoomMax = MathF.Max(zoomMax, d);
                                }
                            }
                            break;
                        case AttributeKind.ColorSub:
                            hasCmyOrCto = hasColorSub = true;
                            break;
                        case AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc:
                            hasCmyOrCto = hasColorTemperature = true;
                            hasCtc |= f.Class.Kind == AttributeKind.Ctc;
                            break;
                        case AttributeKind.ColorCrossfade:
                            hasCrossfade = true;
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
                float perBeam = (float)(cd * _beamShare[beam.Index]);
                float specAngle = hasZoom ? zoomMin : native; // datasheet candela is the narrowest-zoom value
                beam.ReferenceFlux = perBeam * table.SolidAngle(specAngle * DegToRad);
                beam.PhotometrySource = PhotometrySource.SpecsCandela;
            }
            else if (Specs?.LuminousFluxLm is { } lm && lm > 0 && double.IsFinite(lm) && !(bg.LuminousFlux > 0f && bg.LuminousFlux != GdtfDefaultFlux))
            {
                // the GDTF flux is missing or GDTF's default: the researched fixture flux, shared between the beams
                beam.ReferenceFlux = (float)(lm * _beamShare[beam.Index]);
                beam.PhotometrySource = PhotometrySource.SpecsFlux;
            }
            else if (bg.LuminousFlux > 0f && float.IsFinite(bg.LuminousFlux))
            {
                // GDTF's default (10 000 lm) on every pixel of a multi-beam fixture is not 10 000 lm per pixel: read it as
                // the fixture's total
                beam.ReferenceFlux = bg.LuminousFlux == GdtfDefaultFlux && Beams.Count > 1 ? bg.LuminousFlux / Beams.Count : bg.LuminousFlux;
                beam.PhotometrySource = PhotometrySource.GdtfFlux;
            }
            else
            {
                float efficacy = EffectiveLampType(bg, emitterSet.Count > 0) switch
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
                beam.EmitterFullXyz = new Vector3[Emitters.Count];
                for (int e = 0; e < Emitters.Count; e++)
                    if (beam.EmitterSlot[e] >= 0)
                    {
                        var pe = list[beam.EmitterSlot[e]];
                        beam.EmitterFullXyz[e] = pe.TristimulusPerLumen * pe.MaxFlux;
                    }
            }

            // LED engines that mix colour (emitters in this mode, or several coloured emitters in the fixture) or tune their
            // white (CTC, or kelvin CTO/CTB on a wash-type LED) move their white point electronically; CMY channels and the
            // CTO flags of LED spots and profiles are real filters
            beam.ElectronicColor = EffectiveLampType(bg, emitterSet.Count > 0) == LampType.Led && !hasColorSub &&
                (emitterSet.Count > 0 || HasColouredEmitters() || hasCtc || (hasColorTemperature && !IsSpotLike(Category)));
            if (beam.ElectronicColor && beam.Emitters.Length > 0 && (hasColorTemperature || hasCrossfade))
            {
                string key = string.Join(";", beam.Emitters.Select(e => FormattableString.Invariant($"{e.Chromaticity.X:F4},{e.Chromaticity.Y:F4},{e.MaxFlux:G6}")));
                if (!_whiteTables.TryGetValue(key, out var white)) _whiteTables[key] = white = WhiteEfficiencyTable.Create(beam.Emitters);
                beam.WhiteTable = white;
            }
        }

        /// <summary>
        /// The light source of a beam: LED when the mode drives additive emitters in it; else the researched
        /// <c>_specs</c> lightSourceType (GDTF files often keep the default "Discharge", or a wrong type); else the GDTF
        /// LampType.
        /// </summary>
        private LampType EffectiveLampType(BeamGeometry bg, bool hasEmitters)
        {
            if (hasEmitters) return LampType.Led;
            return Specs?.LightSourceType switch
            {
                "LED" => LampType.Led,
                "Discharge Lamp" => LampType.Discharge,
                "Halogen Lamp" => LampType.Halogen,
                "Tungsten Lamp" => LampType.Tungsten,
                _ => bg.LampType,
            };
        }

        /// <summary>Spot-type fixtures, whose CTO/CTB on an LED engine is a mechanical filter rather than a tunable white.</summary>
        private static bool IsSpotLike(FixtureCategory category) => category is FixtureCategory.Profile or FixtureCategory.Spot
            or FixtureCategory.Beam or FixtureCategory.WashBeam or FixtureCategory.Bsw or FixtureCategory.FollowSpot;

        /// <summary>
        /// Share of the fixture's researched output (<c>_specs</c> candela or flux) that each beam carries: proportional to
        /// the beams' GDTF LuminousFlux when the file gives them different values (a main engine and an aura ring, large
        /// and small cells), equal otherwise. A beam whose flux is missing or GDTF's default 10 000 lm counts as the mean
        /// of the beams with a real value.
        /// </summary>
        private void ComputeBeamShares()
        {
            int n = Beams.Count;
            _beamShare = new float[n];
            var flux = new double[n];
            double known = 0;
            int knownCount = 0;
            for (int b = 0; b < n; b++)
            {
                float f = Beams[b].Geometry.LuminousFlux;
                flux[b] = f > 0f && float.IsFinite(f) && f != GdtfDefaultFlux ? f : double.NaN;
                if (double.IsNaN(flux[b])) continue;
                known += flux[b];
                knownCount++;
            }
            double mean = knownCount > 0 ? known / knownCount : 1.0;
            double total = 0;
            for (int b = 0; b < n; b++)
            {
                if (double.IsNaN(flux[b])) flux[b] = mean;
                total += flux[b];
            }
            for (int b = 0; b < n; b++) _beamShare[b] = total > 0 ? (float)(flux[b] / total) : 1f / n;
        }

        /// <summary>True when the fixture type describes at least two visible emitters of clearly different colour (an RGB… engine).</summary>
        private bool HasColouredEmitters()
        {
            if (_colouredEmitters is { } known) return known;
            var points = new List<Vector2>();
            foreach (var e in FixtureType.PhysicalDescriptions.Emitters)
                if (TryEmitterChromaticity(e, out var xy)) points.Add(xy);
            bool coloured = false;
            for (int i = 0; i < points.Count && !coloured; i++)
                for (int j = i + 1; j < points.Count && !coloured; j++)
                    coloured = Vector2.Distance(points[i], points[j]) > 0.1f;
            _colouredEmitters = coloured;
            return coloured;
        }

        /// <summary>
        /// A CTO/CTB whose kelvin range runs from warmer to cooler than the source and whose channel has a separate "Open" /
        /// "No function" range is a colour-temperature selector (its value is the resulting CCT, as for CTC), not a filter
        /// that is neutral at one end of its range.
        /// </summary>
        private void ResolveColorTemperatureSelectors()
        {
            if (Beams.Count == 0) return;
            float source = Beams[0].SourceKelvin;
            foreach (var f in Functions)
            {
                if (f.Class.Kind is not (AttributeKind.Cto or AttributeKind.Ctb) || float.IsNaN(f.NeutralKelvin)) continue;
                float lo = float.NaN, hi = float.NaN;
                if (!float.IsNaN(f.KelvinFrom))
                {
                    lo = MathF.Min(f.KelvinFrom, f.KelvinTo);
                    hi = MathF.Max(f.KelvinFrom, f.KelvinTo);
                }
                else
                {
                    foreach (var set in f.SetArray)
                        if (!float.IsNaN(set.Kelvin))
                        {
                            lo = float.IsNaN(lo) ? set.Kelvin : MathF.Min(lo, set.Kelvin);
                            hi = float.IsNaN(hi) ? set.Kelvin : MathF.Max(hi, set.Kelvin);
                        }
                }
                if (lo < source / 1.03f && hi > source * 1.03f && HasOffState(Channels[f.Channel])) f.NeutralKelvin = float.NaN;
            }
        }

        /// <summary>True when a function of the channel has a channel set meaning "open" or "no function".</summary>
        private bool HasOffState(ProfileChannel ch)
        {
            for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                foreach (var set in Functions[k].SetArray)
                    if ((set.Flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) != 0) return true;
            return false;
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
            // CMY/CTO are motorised flags unless every beam is an LED engine that mixes its colour electronically
            bool electronicColor = Beams.Count > 0 && Beams.All(b => b.ElectronicColor);
            foreach (var f in Functions)
            {
                int slots = f.Wheel >= 0 ? Wheels[f.Wheel].SlotCount : 0;
                (f.Motion, f.Limits) = MotorDynamics.DefaultMotion(f, Category, slots, mechanicalColor: !electronicColor);
                var kind = f.Class.Kind;
                f.MotionGroup = FixtureEvaluator.MotionGroup(f.Class);
                if (kind == AttributeKind.MotionSpeed) f.SpeedGroup = FixtureEvaluator.MotionGroupOf(f.Class.Code);
                f.IsRotation = FixtureEvaluator.IsRotation(kind);
                f.MotorQuantity = FixtureEvaluator.Quantity(kind);
                if (f.Motion == MotionKind.Rotation) f.SpinAcceleration = f.Limits.MaxAcceleration;
                f.ColorCode = kind switch
                {
                    AttributeKind.ColorRgb or AttributeKind.ColorSub => f.Class.Code switch
                    {
                        "R" => 0,
                        "G" => 1,
                        "B" => 2,
                        "C" => 3,
                        "M" => 4,
                        "Y" => 5,
                        _ => -1,
                    },
                    AttributeKind.BladeSoft => f.Class.Code == "B" ? 7 : 6,
                    _ => -1,
                };
            }
        }

        /// <summary>
        /// Decodes every value of each 8-bit channel without mode-master conditions once (shared by the instances of a
        /// GDTF channel, e.g. all pixels), so that the per-frame decode is a table lookup.
        /// </summary>
        private void BuildDecodeTables()
        {
            var shared = new Dictionary<DmxChannel, DecodeEntry[]>(ReferenceEqualityComparer.Instance);
            var fns = Functions.ToArray();
            foreach (var ch in Channels)
            {
                if (ch.ByteCount != 1 || ch.FunctionCount == 0 || ch.FunctionCount > short.MaxValue) continue;
                bool dependent = false;
                for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                    dependent |= fns[k].ModeMaster >= 0 || fns[k].SetArray.Length > short.MaxValue;
                if (dependent) continue;
                if (shared.TryGetValue(ch.Source, out var table) && table.Length == 256 && SameLayout(ch, table))
                {
                    ch.Decode = table;
                    continue;
                }
                table = new DecodeEntry[256];
                for (uint v = 0; v < 256; v++)
                {
                    int fn = FixtureEvaluator.SelectFunction(ch, fns, null, v);
                    var f = fns[fn];
                    int set = f.SetIndexAt(v);
                    float physical = FixtureEvaluator.PhysicalAt(f, v, set);
                    table[v] = new DecodeEntry(fn - ch.FirstFunction, set, f.FlagsOf(set), physical, f.UsesSlotPosition ? f.SlotPositionAt(v) : physical);
                }
                ch.Decode = table;
                shared.TryAdd(ch.Source, table);
            }

            // instances of one GDTF channel compile the same function list; check before sharing anyway
            bool SameLayout(ProfileChannel ch, DecodeEntry[] table)
            {
                for (int v = 0; v < 256; v++)
                    if (table[v].Function >= ch.FunctionCount) return false;
                return true;
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
