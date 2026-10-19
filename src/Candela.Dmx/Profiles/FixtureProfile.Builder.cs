using System.Globalization;
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

        /// <summary>
        /// Largest field-to-beam angle ratio taken from a file: soft-edged washes and PARs reach about 3; larger ratios in
        /// the library (59 beams, up to 50) are angles that are not a beam/field pair.
        /// </summary>
        private const float MaxFieldRatio = 4f;

        /// <summary>GDTF's default Beam BeamAngle and FieldAngle, degrees.</summary>
        private const float GdtfDefaultAngle = 25f;

        /// <summary>GDTF's default Beam ColorTemperature, K.</summary>
        private const float GdtfDefaultKelvin = 6000f;

        /// <summary>GDTF's default Beam PowerConsumption, W.</summary>
        private const float GdtfDefaultPower = 1000f;

        /// <summary>
        /// True when the file gives the beam a real luminous flux: written, positive, and not GDTF's default (GDTF Builder
        /// writes every Beam attribute, so a written 10 000 lm is most likely the untouched default).
        /// </summary>
        private static bool HasRealFlux(BeamGeometry bg) =>
            bg.IsLuminousFluxSpecified && bg.LuminousFlux > 0f && float.IsFinite(bg.LuminousFlux) && bg.LuminousFlux != GdtfDefaultFlux;

        /// <summary>True when the file gives the beam a real electrical power (written, positive, not GDTF's 1000 W default).</summary>
        private static bool HasRealPower(BeamGeometry bg) =>
            bg.IsPowerConsumptionSpecified && bg.PowerConsumption > 0f && float.IsFinite(bg.PowerConsumption) && bg.PowerConsumption != GdtfDefaultPower;
        private readonly Dictionary<ChainKey, int> _instanceIndex = new();
        private readonly Dictionary<DmxChannel, List<int>> _instancesOf = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Wheel, int> _wheelIndex = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _emitterIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<BeamGeometry, BeamSolidAngleTable> _solidAngleCache = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, WhiteEfficiencyTable?> _whiteTables = new(StringComparer.Ordinal);
        private readonly HashSet<string> _whiteWarned = new(StringComparer.Ordinal);
        private bool? _colouredEmitters;
        private int[] _beamFirst = Array.Empty<int>(); // per channel: first beam index affected
        private int[] _beamCount = Array.Empty<int>(); // per channel: number of beams affected (contiguous)
        private int[] _subtreeSize = Array.Empty<int>();
        private float[] _beamShare = Array.Empty<float>(); // per beam: share of the fixture's specs flux and power
        private float[] _peakShare = Array.Empty<float>(); // per beam: share of the fixture's specs peak candela
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
        public int[] FollowerOrder { get; private set; } = Array.Empty<int>();
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
                InheritSiblingWheels(pc);
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

        /// <summary>
        /// A rotation, index, spin or shake function written without its wheel, on the channel of a selection function
        /// of the same mechanism that names it (Elation Artiste Picasso "AnimationWheel1PosRotate" next to
        /// "AnimationWheel1"), turns that wheel: the mechanism is in the beam while it rotates. Selection functions inherit
        /// the wheel only when it has a single slot (an animation disc, a single prism), whose position needs no slot map.
        /// </summary>
        private void InheritSiblingWheels(ProfileChannel ch)
        {
            int end = ch.FirstFunction + ch.FunctionCount;
            MarkPlainOpenSets(ch);
            for (int k = ch.FirstFunction; k < end; k++)
            {
                var f = Functions[k];
                if (f.Class.Wheel == WheelFamily.None) continue;
                for (int j = ch.FirstFunction; j < end && !f.OnSelectionChannel; j++)
                {
                    var g = Functions[j];
                    f.OnSelectionChannel = j != k && g.Class.Wheel == f.Class.Wheel && g.Class.Number == f.Class.Number &&
                        (IsWheelSelection(g.Class.Kind) || g.Class.Kind == AttributeKind.AnimationInsertion);
                }
                f.ImpliesInsertion = f.Class.Kind is not (AttributeKind.SlotIndex or AttributeKind.SlotRotate or AttributeKind.SlotShake
                        or AttributeKind.EffectRate) ||
                    f.Class.Wheel is not (WheelFamily.Prism or WheelFamily.Animation or WheelFamily.AnimationSystem or WheelFamily.BeamShaper) ||
                    f.OnSelectionChannel || Array.Exists(f.SetArray, s => s.WheelSlot > 0);
                if (f.Wheel >= 0) continue;
                for (int j = ch.FirstFunction; j < end; j++)
                {
                    var g = Functions[j];
                    if (g.Wheel < 0 || g.Class.Wheel != f.Class.Wheel || g.Class.Number != f.Class.Number) continue;
                    if (IsWheelSelection(f.Class.Kind) && Wheels[g.Wheel].SlotCount > 1) break;
                    f.Wheel = g.Wheel;
                    break;
                }
            }
        }

        /// <summary>
        /// A channel whose selection functions have no range that takes the mechanism out, but a range named plainly
        /// "Open" that selects a slot which is not a hole (PRstage Spot 460 animation wheel: "Open" → "Wheel Slot 1"): the
        /// file put the open position on a placeholder slot, so that range takes the mechanism out. Where the channel
        /// does have an out range (ACME Sana: "Close" selects the hole, "Open" the 8-facet prism) the slots decide.
        /// </summary>
        private void MarkPlainOpenSets(ProfileChannel ch)
        {
            int end = ch.FirstFunction + ch.FunctionCount;
            bool selection = false;
            for (int k = ch.FirstFunction; k < end; k++)
            {
                var f = Functions[k];
                if (!IsWheelSelection(f.Class.Kind) || f.Wheel < 0) continue;
                selection = true;
                if ((f.NameFlags & ChannelSetFlags.WheelOut) != 0 || Array.Exists(f.SetFlagArray, s => (s & ChannelSetFlags.WheelOut) != 0)) return;
            }
            if (!selection) return;
            for (int k = ch.FirstFunction; k < end; k++)
            {
                var f = Functions[k];
                if (!IsWheelSelection(f.Class.Kind) || f.Wheel < 0) continue;
                bool changed = false;
                foreach (var s in f.SetArray)
                {
                    if (!NameParsing.IsPlainOpenSlotName(s.Name)) continue;
                    s.Flags |= ChannelSetFlags.WheelOut;
                    changed = true;
                }
                if (changed)
                {
                    f.SetSets(f.SetArray);
                    Warnings.Add($"{f.Source.FullName}: a range named 'Open' selects a slot that is not open; read as open.");
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

        /// <summary><see cref="DmxValue.ToRangeEnd"/>, 0 for an invalid byte count.</summary>
        private static uint SafeRangeEnd(DmxValue v, int bytes)
        {
            try
            {
                return v.ToRangeEnd(bytes);
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
            if (cls.Kind == AttributeKind.Dimmer && ch.Class.Kind != AttributeKind.Dimmer && !string.IsNullOrEmpty(ch.Attribute))
            {
                // a Dimmer function on a channel of another attribute (control/function channels, background dimmers, a
                // hue channel whose function carries the wrong attribute) must not dim the beam; a colour-system channel
                // keeps its own meaning
                cls = ch.Class.Kind is AttributeKind.HsbHue or AttributeKind.HsbSaturation or AttributeKind.HsbBrightness or AttributeKind.CieX
                    or AttributeKind.CieY or AttributeKind.CieBrightness
                    ? ch.Class
                    : AttributeClass.Ignored;
                Warnings.Add($"{src.FullName}: Dimmer function on a '{ch.Attribute}' channel read as {(cls.Kind == AttributeKind.Ignored ? "no intensity" : ch.Attribute)}.");
            }
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
            if (src.Wheel is { } wheel) f.Wheel = WheelIndexOf(wheel, cls.Wheel);
            // a wheel selection function named "Open", "Off", "Removed" … that selects no slot keeps the wheel out of the
            // beam wherever no named channel set says otherwise (ACME Chiron "Prism1" function "Open")
            bool selects = IsWheelSelection(cls.Kind) || cls.Kind == AttributeKind.AnimationInsertion;
            bool selectsSlot = selects && src.ChannelSets.Exists(s => s.WheelSlotIndex > 0);
            if (selects && !selectsSlot && NameParsing.IsWheelOutName(src.Name)) nameFlags |= ChannelSetFlags.WheelOut;
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
                if (cls.Kind == AttributeKind.Shutter && (setFlags & ChannelSetFlags.Closed) != 0 && IsStrobeOff(src, s.Name))
                    setFlags = (setFlags & ~ChannelSetFlags.Closed) | ChannelSetFlags.Open;
                // a selection range named "Open", "Off", "Removed" … that selects no slot, or an open one, takes the
                // mechanism out of the beam (Rivale AnimationWheel1 "open", Elation Artiste Picasso "OPEN"); in a rotation,
                // index or spin function of the mechanism such a range ("No function") leaves the mechanism alone
                // A set that selects a slot of a wheel with several slots is decided by that slot, not its name (ACME Sana's
                // prism "Close" selects the hole and "Open" the 8-facet prism); on a single-slot wheel the slot index says
                // nothing (PRstage Spot 460 "Open" → slot 1, the animation disc)
                if ((selects || cls.Wheel != WheelFamily.None) && !string.IsNullOrWhiteSpace(s.Name) && NameParsing.IsWheelOutName(s.Name) &&
                    (s.WheelSlotIndex <= 0 || f.Wheel < 0 || Wheels[f.Wheel].SlotCount <= 1 || IsOpenSlot(f.Wheel, s.WheelSlotIndex)))
                    setFlags |= ChannelSetFlags.WheelOut;
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
            if (cls.Kind == AttributeKind.Dimmer) ReconcileDimmerNames(f, sets);
            if (IsWheelSelection(cls.Kind) && !selectsSlot && (f.Wheel < 0 || Wheels[f.Wheel].SlotCount <= 1)) MarkInsertionRamps(sets);
            if (IsWheelSelection(cls.Kind)) MarkInsertionCycles(sets);
            f.SetSets(sets.ToArray());

            float maxAbs = MathF.Max(MathF.Abs(f.PhysicalFrom), MathF.Abs(f.PhysicalTo));
            foreach (var s in sets)
                if (s.HasPhysical) maxAbs = MathF.Max(maxAbs, MathF.Max(MathF.Abs(s.PhysicalFrom), MathF.Abs(s.PhysicalTo)));
            f.LevelScale = maxAbs > 1.5f ? (maxAbs <= 100.5f ? 100f : maxAbs) : 1f;

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

        /// <summary>Wheel slot selection attributes (plain, with spin, with shake).</summary>
        private static bool IsWheelSelection(AttributeKind kind) =>
            kind is AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake;

        /// <summary>True when 1-based slot <paramref name="slot"/> of profile wheel <paramref name="wheel"/> is open (a hole); false without a wheel.</summary>
        private bool IsOpenSlot(int wheel, int slot)
        {
            if (wheel < 0) return false;
            var open = Wheels[wheel].SlotOpenArray;
            return slot >= 1 && slot <= open.Length && open[slot - 1];
        }

        /// <summary>
        /// Marks the ranges in which the mechanism keeps moving in and out of the beam ("Ramping from open to full position
        /// max" … unnamed … "… min": the animation-wheel ramps of Robe fixtures): a named end, the unnamed ranges after it
        /// and the other named end of the same ramp form one group whose cycle rate runs from fast ("max") to slow.
        /// </summary>
        private static void MarkInsertionCycles(List<ProfileChannelSet> sets)
        {
            int i = 0;
            while (i < sets.Count)
            {
                if (!NameParsing.TryParseInsertionCycle(sets[i].Name, out float from, out float to, out int end))
                {
                    i++;
                    continue;
                }
                int j = i + 1;
                while (j < sets.Count && string.IsNullOrWhiteSpace(sets[j].Name)) j++;
                int last = j < sets.Count && NameParsing.TryParseInsertionCycle(sets[j].Name, out float from2, out float to2, out int end2) &&
                    from2 == from && to2 == to && end2 != 0 && end2 != end
                    ? j
                    : j - 1;
                uint groupFrom = sets[i].DmxFrom, groupTo = sets[last].DmxTo;
                for (int k = i; k <= last; k++)
                {
                    var s = sets[k];
                    s.Flags = (s.Flags | ChannelSetFlags.InsertionCycle) & ~ChannelSetFlags.WheelOut;
                    s.CycleFrom = from;
                    s.CycleTo = to;
                    s.CycleDmxFrom = groupFrom;
                    s.CycleDmxTo = groupTo;
                    s.CycleFastFirst = end >= 0;
                }
                i = last + 1;
            }
        }

        /// <summary>
        /// Marks the ranges of a single-slot selection function (an animation wheel, a one-prism wheel) that move the
        /// mechanism gradually into the beam: a range right after an "out" range that has no name ("Open", "", "Animation
        /// In": Cameo Oron H2, EK H1), names a transition out of the open state ("Open to Dots": Elation Proteus Hybrid
        /// Max) or an insertion "in proportion" (ADJ Protege). Insertion runs 0 → 1 across such a range.
        /// </summary>
        private static void MarkInsertionRamps(List<ProfileChannelSet> sets)
        {
            for (int i = 1; i < sets.Count; i++)
            {
                var previous = sets[i - 1];
                var set = sets[i];
                if ((previous.Flags & ChannelSetFlags.WheelOut) == 0 || (set.Flags & ChannelSetFlags.WheelOut) != 0) continue;
                if (set.DmxTo > set.DmxFrom && NameParsing.IsInsertionRampName(set.Name)) set.Flags |= ChannelSetFlags.InsertionRamp;
            }
        }

        /// <summary>True for GDTF's default physical range 0 … 1 (either direction).</summary>
        private static bool IsDefaultRange(float from, float to) => (from == 0f && to == 1f) || (from == 1f && to == 0f);

        /// <summary>
        /// A shutter channel set named just "Off" inside a strobe function ("Strobe Off", "Strobe") or a function whose
        /// physical value is a constant open shutter: the strobe is off and the shutter open, not "light off" (1060 sets
        /// of 15 files in the library).
        /// </summary>
        private static bool IsStrobeOff(ChannelFunction function, string setName)
        {
            if (!string.Equals(setName.Trim(), "off", StringComparison.OrdinalIgnoreCase)) return false;
            if (function.Name.Contains("strobe", StringComparison.OrdinalIgnoreCase)) return true;
            float from = Finite(function.PhysicalFrom, 0f), to = Finite(function.PhysicalTo, 1f);
            return from == to && from >= 0.99f;
        }

        /// <summary>
        /// Channel sets named "Open"/"Closed" on a dimmer that contradict its physical range (e.g. "Closed" at DMX 255 of
        /// a 0 → 1 dimmer, Ayrton MagicPanel 602, ACME AM-WASH300, Prolights PixieWash): the one of the two that follows
        /// the DMX convention — more DMX is brighter — is believed. An increasing physical range drops the contradicting
        /// names; a decreasing one whose names all contradict it (closed at DMX 0, open at the top: LIGHT SKY IPL1015) is
        /// reversed. A decreasing range whose names agree with it is an inverted dimmer and stays.
        /// </summary>
        private void ReconcileDimmerNames(ProfileFunction f, List<ProfileChannelSet> sets)
        {
            float lo = MathF.Min(f.PhysicalFrom, f.PhysicalTo), span = MathF.Abs(f.PhysicalTo - f.PhysicalFrom);
            if (!(span > 1e-6f) || f.DmxTo <= f.DmxFrom) return;
            int flagged = 0, contradicting = 0;
            var contradicts = new bool[sets.Count];
            for (int i = 0; i < sets.Count; i++)
            {
                var s = sets[i];
                bool open = (s.Flags & ChannelSetFlags.Open) != 0, closed = (s.Flags & ChannelSetFlags.Closed) != 0;
                if (open == closed) continue;
                flagged++;
                float physical = s.HasPhysical
                    ? 0.5f * (s.PhysicalFrom + s.PhysicalTo)
                    : f.PhysicalAt((uint)(((ulong)s.DmxFrom + s.DmxTo) / 2));
                bool bright = (physical - lo) / span >= 0.5f;
                if (closed == bright)
                {
                    contradicts[i] = true;
                    contradicting++;
                }
            }
            if (contradicting == 0) return;
            if (f.PhysicalTo < f.PhysicalFrom && contradicting == flagged)
            {
                (f.PhysicalFrom, f.PhysicalTo) = (f.PhysicalTo, f.PhysicalFrom);
                Warnings.Add($"{f.Source.FullName}: dimmer range runs against its open/closed channel sets; reversed.");
                return;
            }
            for (int i = 0; i < sets.Count; i++)
                if (contradicts[i]) sets[i].Flags &= ~(ChannelSetFlags.Open | ChannelSetFlags.Closed);
            Warnings.Add($"{f.Source.FullName}: {contradicting} open/closed channel set name(s) contradict the dimmer's physical range; the range is used.");
        }

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
        /// so a wheel turning through "Open" keeps moving in one direction. On a two-slot wheel (Ayrton Rivale's beam
        /// shaper: "Open", "Beam Shaper") 1 → 2 is the next slot, not a step back across the end.
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
                if (prevK > 0 && slotCount > 2)
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
                // The reader resolves the mode range in the master's resolution with range semantics (a ModeTo written
                // with fewer bytes than the master covers its whole step, so adjacent ranges leave no gap).
                uint from, to;
                if (ReferenceEquals(src.ModeMasterChannel, m.Instance.Channel) && src.ModeMasterChannel.Resolution == m.ByteCount)
                {
                    from = src.ModeFromValue;
                    to = src.ModeToValue;
                }
                else
                {
                    from = SafeResolution(src.ModeFrom, m.ByteCount);
                    to = SafeRangeEnd(src.ModeTo, m.ByteCount);
                    if (to < from) (from, to) = (SafeResolution(src.ModeTo, m.ByteCount), SafeRangeEnd(src.ModeFrom, m.ByteCount));
                }
                f.ModeMaster = master;
                f.ModeFrom = from;
                f.ModeTo = to;
            }
        }

        /// <summary>
        /// Resolves the mode's relations to channel instances (per geometry-reference chain), reads a dimmer's
        /// <c>Override</c> of a level as <c>Multiply</c> (a virtual dimmer written with the wrong type: replacing a
        /// colour by the dimmer level would make the colour channels inoperative), drops relations that close a cycle and
        /// orders the followers so that every master is evaluated before its followers (relation chains such as
        /// grand master → pixel dimmer → pixel colour).
        /// </summary>
        private void BuildRelations()
        {
            var follower = new List<int>[Channels.Count];
            IsRelationMaster = new bool[Channels.Count];
            int reinterpreted = 0;
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
                    var type = r.Type;
                    if (type == RelationType.Override && IsIntensityMaster(Channels[mi]) && IsLevelKind(Functions[fn].Class.Kind))
                    {
                        type = RelationType.Multiply;
                        reinterpreted++;
                    }
                    (follower[fi] ??= new List<int>()).Add(Relations.Count);
                    Relations.Add(new ProfileRelation(mi, fi, fn, type));
                }
            }
            if (reinterpreted > 0)
                Warnings.Add($"{reinterpreted} Override relation(s) of a dimmer over a level read as Multiply (virtual dimmer).");

            FollowerRelations = new int[Channels.Count][];
            for (int i = 0; i < Channels.Count; i++) FollowerRelations[i] = follower[i]?.ToArray() ?? Array.Empty<int>();
            OrderFollowers();
            foreach (int c in FollowerOrder)
                foreach (int ri in FollowerRelations[c])
                    if (Relations[ri].Type == RelationType.Multiply) IsRelationMaster[Relations[ri].Master] = true;
        }

        /// <summary>
        /// Followers in evaluation order: every channel after the masters it follows (Kahn's algorithm). When the
        /// remaining followers all wait for each other, a cycle is found by walking from the first waiting follower to
        /// a master it waits for until a channel repeats, and only the relations of one edge of that cycle are dropped
        /// (an Override edge before a Multiply one, then the lowest-index follower's) — followers downstream of a cycle
        /// keep theirs.
        /// </summary>
        private void OrderFollowers()
        {
            int n = Channels.Count;
            var pending = new int[n]; // relations whose master is itself a follower that is not yet ordered
            var dependents = new List<int>?[n];
            var isFollower = new bool[n];
            int followers = 0;
            for (int c = 0; c < n; c++)
                if (isFollower[c] = FollowerRelations[c].Length > 0) followers++;
            for (int c = 0; c < n; c++)
            {
                foreach (int ri in FollowerRelations[c])
                {
                    int m = Relations[ri].Master;
                    if (!isFollower[m]) continue;
                    pending[c]++;
                    (dependents[m] ??= new List<int>()).Add(c);
                }
            }
            var order = new List<int>(followers);
            var placed = new bool[n];
            var queue = new Queue<int>();
            for (int c = 0; c < n; c++)
                if (isFollower[c] && pending[c] == 0) queue.Enqueue(c);
            int dropped = 0, scan = 0;
            while (order.Count < followers)
            {
                if (queue.Count == 0)
                {
                    while (!isFollower[scan] || placed[scan]) scan++;
                    var (c, master) = CycleEdge(scan, placed, isFollower);
                    var keep = new List<int>(FollowerRelations[c].Length);
                    foreach (int ri in FollowerRelations[c])
                    {
                        if (Relations[ri].Master != master)
                        {
                            keep.Add(ri);
                            continue;
                        }
                        dropped++;
                        pending[c]--;
                        dependents[master]!.Remove(c);
                    }
                    FollowerRelations[c] = keep.ToArray();
                    if (pending[c] == 0) queue.Enqueue(c);
                    continue;
                }
                int next = queue.Dequeue();
                placed[next] = true;
                order.Add(next);
                if (dependents[next] is not { } deps) continue;
                foreach (int d in deps)
                    if (--pending[d] == 0) queue.Enqueue(d);
            }
            if (dropped > 0) Warnings.Add($"{dropped} relation(s) closing a cycle of relations ignored.");
            FollowerOrder = order.Where(c => FollowerRelations[c].Length > 0).ToArray();
        }

        /// <summary>
        /// A relation edge on a cycle among the followers not yet ordered, starting the walk at <paramref name="start"/>
        /// (every such follower waits for at least one master that is itself an unordered follower): the cycle's
        /// lowest-index channel and its master on the cycle.
        /// </summary>
        private (int Follower, int Master) CycleEdge(int start, bool[] placed, bool[] isFollower)
        {
            var path = new List<int>();
            var at = new Dictionary<int, int>();
            int x = start;
            while (!at.ContainsKey(x))
            {
                at[x] = path.Count;
                path.Add(x);
                int waitsFor = -1;
                foreach (int ri in FollowerRelations[x])
                {
                    int m = Relations[ri].Master;
                    if (isFollower[m] && !placed[m])
                    {
                        waitsFor = m;
                        break;
                    }
                }
                x = waitsFor;
            }
            // path[at[x]] … path[^1] is the cycle; path[i] follows path[i + 1], and the last follows x. An Override edge is
            // dropped in preference to a Multiply one (a pixel dimmer that both scales its colour and is overridden by it
            // keeps the dimming), then the lowest-index follower's.
            int first = at[x], best = -1;
            bool bestOverride = false;
            for (int i = first; i < path.Count; i++)
            {
                int master = i + 1 < path.Count ? path[i + 1] : x;
                bool isOverride = false;
                foreach (int ri in FollowerRelations[path[i]])
                    if (Relations[ri].Master == master && Relations[ri].Type == RelationType.Override) isOverride = true;
                if (best < 0 || (isOverride && !bestOverride) || (isOverride == bestOverride && path[i] < path[best]))
                {
                    best = i;
                    bestOverride = isOverride;
                }
            }
            return (path[best], best + 1 < path.Count ? path[best + 1] : x);
        }

        /// <summary>A channel whose attribute is an intensity (Dimmer): its relations scale their followers.</summary>
        private bool IsIntensityMaster(ProfileChannel ch)
        {
            if (ch.Class.Kind == AttributeKind.Dimmer) return true;
            for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                if (Functions[k].Class.Kind == AttributeKind.Dimmer) return true;
            return false;
        }

        /// <summary>Attributes whose value is a level 0 … 1 that an intensity master can scale.</summary>
        private static bool IsLevelKind(AttributeKind kind) =>
            kind is AttributeKind.Dimmer or AttributeKind.ColorAdd or AttributeKind.ColorRgb or AttributeKind.HsbBrightness
                or AttributeKind.CieBrightness;

        // ------------------------------------------------------------------ wheels and emitters

        /// <summary>
        /// The profile wheel of a GDTF wheel, compiled on first use with the family of the function that uses it first
        /// (colour, gobo, prism, animation, effects, beam shaper). A slot is open — a hole, no filter, gobo, prism or
        /// animation — when it has no facets and no animation system and either has no media and an open name or GDTF's
        /// default colour (D65 at 100 %, which files leave on colour, gobo and prism slots without a filter; not on beam
        /// shaper or animation slots, whose lens or disc has no colour), or names itself plainly open next to an image of
        /// the aperture ("Open" with media "G1": CLF Aorun, Cameo AZOR).
        /// </summary>
        private int WheelIndexOf(Wheel wheel, WheelFamily family)
        {
            if (_wheelIndex.TryGetValue(wheel, out int i)) return i;
            int n = Math.Max(1, wheel.Slots.Count);
            var transmission = new Vector3[n];
            var facets = new int[n];
            var open = new bool[n];
            var stretch = new float[n];
            Array.Fill(stretch, 1f);
            var colourXyz = new Vector3[n];
            Array.Fill(colourXyz, ColorSpaces.XyToXyz(ColorSpaces.D65WhiteXy));
            bool defaultColourIsOpen = family is not (WheelFamily.BeamShaper or WheelFamily.Animation or WheelFamily.AnimationSystem);
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
                if (float.IsFinite(c.X) && float.IsFinite(c.Y) && c.Y > 0f && c.X >= 0f && c.X + c.Y <= 1.0001f)
                    colourXyz[s] = ColorSpaces.XyToXyz(new Vector2(c.X, c.Y));
                // a single facet is not a prism: it passes the beam on (BeamZ Ignite's "Wheel Slot 1" under "Prism1 OPEN")
                facets[s] = slot.Facets.Count > 1 ? slot.Facets.Count : slot.Facets.Count == 1 ? 0 : NameParsing.ParseFacetCount(slot.Name);
                if (facets[s] == 1) facets[s] = 0;
                bool media = slot.MediaPath is not null || slot.MediaFileName is not null;
                bool defaultColour = y >= 0.99f && MathF.Abs(c.X - 0.3127f) < 0.01f && MathF.Abs(c.Y - 0.329f) < 0.01f;
                open[s] = slot.Facets.Count <= 1 && slot.AnimationSystem is null && facets[s] == 0 &&
                    (media
                        ? NameParsing.IsPlainOpenSlotName(slot.Name) || IsOpenMediaName(slot.MediaFileName)
                        : NameParsing.IsOpenSlotName(slot.Name) || (defaultColourIsOpen && defaultColour) ||
                            (NameParsing.IsWheelOutName(slot.Name) && !slot.Name.Contains("clos", StringComparison.OrdinalIgnoreCase)));
                // an open slot is a hole: files often store the lamp's own colour there, which is not a filter
                if (open[s] && y >= 0.9f) transmission[s] = Vector3.One;
                // a beam-shaper slot without an image is an anamorphic lens that stretches the beam (Ayrton Rivale)
                if (family == WheelFamily.BeamShaper && !open[s] && !media && facets[s] == 0) stretch[s] = NameParsing.ParseStretch(slot.Name);
            }
            i = Wheels.Count;
            Wheels.Add(new ProfileWheel(i, wheel, transmission, facets, open, stretch, colourXyz));
            _wheelIndex[wheel] = i;
            return i;
        }

        /// <summary>A media file named as an open aperture ("Open", "open_1", "Open-2"): the slot is a hole.</summary>
        private static bool IsOpenMediaName(string? media)
        {
            if (string.IsNullOrWhiteSpace(media)) return false;
            var s = media.AsSpan().Trim();
            if (!s.StartsWith("open", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (char c in s[4..])
                if (char.IsLetter(c)) return false;
            return true;
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

            var excluded = ExcludedRelationMasters();

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

            MarkUnaddressedBeams();
            ComputeBeamShares();
            int warningsBefore = Warnings.Count;
            SetupAllBeamPhotometry();
            // the datasheet peak is the beams' peaks together: shared by each beam's own peak (its flux over its solid
            // angle), not by flux, once the optics are known — then the beams again (same optics, so the same warnings)
            if (PeakSharesByOptics() is { } byOptics)
            {
                Warnings.RemoveRange(warningsBefore, Warnings.Count - warningsBefore);
                _peakShare = byOptics;
                SetupAllBeamPhotometry();
            }
            if (DatasheetCandelaPerBeam() is { } perBeam)
            {
                Warnings.RemoveRange(warningsBefore, Warnings.Count - warningsBefore);
                _peakShare = perBeam.Shares;
                SetupAllBeamPhotometry();
                Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"The datasheet peak ({FormatCandela(Specs!.MaxLuminousIntensityCd!.Value)} cd) matches one beam of the file ({FormatCandela(perBeam.FileBeamCd)} cd), not its {Beams.Count} beams together ({FormatCandela(perBeam.FileTotalCd)} cd), and shared between them it would make {perBeam.SharedEfficacy:F1} lm/W: read per beam ({perBeam.Efficacy:F0} lm/W)."));
            }
        }

        /// <summary>Attributes that give a beam its colour (additive emitters, indirect colour systems).</summary>
        private static bool IsColourSource(AttributeKind kind) =>
            kind is AttributeKind.ColorAdd or AttributeKind.ColorRgb or AttributeKind.HsbHue or AttributeKind.HsbSaturation
                or AttributeKind.HsbBrightness or AttributeKind.CieX or AttributeKind.CieY or AttributeKind.CieBrightness;

        /// <summary>
        /// An LED ring or cell that other modes of the fixture colour with additive emitter channels, but that no colour
        /// channel of this mode reaches while the mode does mix other beams' emitters, has no light of its own here: there
        /// is no white lamp behind it (Ayrton Zonda 3 FX "Basic": the "Liquid Effect" ring, driven by "Zonda Effect"
        /// RGB channels only in the "+ Liquid" modes). Such beams are <see cref="ProfileBeam.Unaddressed"/> and stay dark;
        /// a mode without any emitter channel (a one-channel dimmer mode of an RGBW wash) still lights its beams white.
        /// </summary>
        private void MarkUnaddressedBeams()
        {
            if (Beams.Count < 2 || FixtureType.DmxModes.Count < 2) return;
            bool modeMixes = false;
            foreach (var f in Functions)
                if (f.Class.Kind == AttributeKind.ColorAdd && f.Emitter >= 0) modeMixes = true;
            if (!modeMixes) return;

            HashSet<GdtfGeometry>? elsewhere = null;
            foreach (var beam in Beams)
            {
                // only decorative glow LEDs (aura, background, liquid-effect rings) are off when no channel addresses them; a
                // cell with channels of its own (a white strobe cell's dimmer) is addressed, whatever its colour
                if (beam.Geometry.BeamType != GdtfBeamType.Glow || beam.OwnChannels.Length > 0) continue;
                bool reached = false;
                foreach (int c in ChannelsOfBeam(beam))
                {
                    var ch = Channels[c];
                    for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount && !reached; k++)
                        reached = IsColourSource(Functions[k].Class.Kind);
                    if (reached) break;
                }
                if (reached) continue;
                if (elsewhere is null)
                {
                    elsewhere = new HashSet<GdtfGeometry>(ReferenceEqualityComparer.Instance);
                    foreach (var mode in FixtureType.DmxModes)
                    {
                        if (ReferenceEquals(mode, Mode)) continue;
                        foreach (var channel in mode.Channels)
                        {
                            if (channel.Geometry is not { } g) continue;
                            foreach (var lc in channel.LogicalChannels)
                                foreach (var fn in lc.Functions)
                                    if (AttributeClassifier.Classify(fn.AttributeName ?? "").Kind == AttributeKind.ColorAdd) elsewhere.Add(g);
                        }
                    }
                }
                var parts = Geometry.Parts;
                for (int part = beam.PartIndex; part >= 0; part = parts[part].ParentIndex)
                {
                    if (!elsewhere.Contains(parts[part].Geometry)) continue;
                    beam.Unaddressed = true;
                    Warnings.Add($"Beam '{beam.Name}' is coloured by emitter channels in other modes but by no channel of mode '{Mode.Name}': it stays dark.");
                    break;
                }
            }
        }

        private void SetupAllBeamPhotometry()
        {
            _whiteWarned.Clear();
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
                    beam.FieldRatioEstimated = true;
                    beam.SolidAngles = new BeamSolidAngleTable(PhotometryBeamType.Wash, beam.NativeBeamAngle, beam.NativeBeamAngle, beam.FieldRatio);
                    beam.ReferenceFlux = 1000f;
                    beam.PhotometrySource = PhotometrySource.Estimated;
                    beam.SourceKelvin = 6000f;
                }
            }
        }

        /// <summary>
        /// Shares of the datasheet peak (<c>_specs</c> <c>maxLuminousIntensityCd</c>, the fixture's on-axis intensity at its
        /// narrowest zoom) once the beams' optics are known: in proportion to each beam's own peak — its flux share
        /// (<see cref="ComputeBeamShares"/>) over its solid angle at the datasheet's angle — so that the beams' peaks add up
        /// to the datasheet value with their fluxes in the file's proportions. Shared by flux alone, a diffuse beam (a
        /// Lambertian glow: aura backlight, liquid-effect ring, pixel dots around the lens) would get a narrow cell's peak
        /// and emit it over π sr (Ayrton Zonda 3 FX "Basic": 180 000 lm from its liquid effect next to 7 × 170 lm pixels).
        /// Returns null when the shares do not change (one beam, beams of the same optics, no datasheet peak).
        /// </summary>
        private float[]? PeakSharesByOptics()
        {
            if (Beams.Count < 2 || Specs?.MaxLuminousIntensityCd is not { } cd || !(cd > 0) || !double.IsFinite(cd)) return null;
            var weight = new double[Beams.Count];
            double total = 0;
            for (int b = 0; b < Beams.Count; b++)
            {
                var beam = Beams[b];
                if (beam.PhotometrySource != PhotometrySource.SpecsCandela || !(_peakShare[b] > 0)) return null;
                double omega = beam.ReferenceFlux / (cd * _peakShare[b]); // reference flux = (cd × share) × Ω
                if (!(omega > 0) || !double.IsFinite(omega)) return null;
                weight[b] = _beamShare[b] / omega;
                total += weight[b];
            }
            if (!(total > 0) || !double.IsFinite(total)) return null;
            var shares = new float[Beams.Count];
            bool changed = false;
            for (int b = 0; b < Beams.Count; b++)
            {
                shares[b] = (float)(weight[b] / total);
                changed |= MathF.Abs(shares[b] - _peakShare[b]) > 1e-4f * MathF.Max(shares[b], _peakShare[b]);
            }
            return changed ? shares : null;
        }

        /// <summary>A luminous intensity for a message: whole candela, or three significant digits below 100 cd.</summary>
        private static string FormatCandela(double cd) =>
            cd >= 100 ? cd.ToString("F0", CultureInfo.InvariantCulture) : cd.ToString("G3", CultureInfo.InvariantCulture);

        /// <summary>Plausible luminous efficacy of a whole fixture (emitted lumens per electrical watt), lm/W.</summary>
        private const double MinEfficacy = 3, MaxEfficacy = 150;

        /// <summary>
        /// Datasheet candela of a multi-cell fixture that is the peak of one cell, not of the fixture: <c>_specs</c> gives
        /// one value per fixture, read as the cells' sum and shared between them (<see cref="ComputeBeamShares"/>), but some
        /// datasheets (pixel panels and dot matrices: Ayrton MagicPanel 602, 20 000 cd for 36 pixels) give the peak of a
        /// cell. Recognised when every beam has a real GDTF flux and all of these hold: the datasheet value is within
        /// a factor of 2 of the brightest beam's own peak (GDTF flux over the beam's solid angle at the datasheet's angle),
        /// it is less than a tenth of the beams' peaks together, and shared it would make the fixture emit less than
        /// <see cref="MinEfficacy"/> lm per watt of its power (<c>_specs</c> power, else the beams' GDTF power) while per
        /// beam it emits a plausible amount (up to <see cref="MaxEfficacy"/> lm/W). Returns the per-beam shares (each beam's
        /// own peak relative to the brightest), or null.
        /// </summary>
        private (float[] Shares, double FileBeamCd, double FileTotalCd, double SharedEfficacy, double Efficacy)? DatasheetCandelaPerBeam()
        {
            if (Beams.Count < 2 || Specs?.MaxLuminousIntensityCd is not { } cd || !(cd > 0) || !double.IsFinite(cd)) return null;
            double power = Specs.PowerConsumptionW is { } w && w > 0 && double.IsFinite(w) ? w : 0;
            var fileCd = new double[Beams.Count];
            double fileMax = 0, fileTotal = 0, sharedFlux = 0, gdtfPower = 0;
            bool allPowered = true;
            for (int b = 0; b < Beams.Count; b++)
            {
                var beam = Beams[b];
                var bg = beam.Geometry;
                if (beam.PhotometrySource != PhotometrySource.SpecsCandela || !HasRealFlux(bg) || !(_peakShare[b] > 0)) return null;
                // the beam's solid angle at the datasheet's angle: its reference flux is (cd × share) × Ω
                double omega = beam.ReferenceFlux / (cd * _peakShare[b]);
                if (!(omega > 0) || !double.IsFinite(omega)) return null;
                fileCd[b] = bg.LuminousFlux / omega;
                fileMax = Math.Max(fileMax, fileCd[b]);
                fileTotal += fileCd[b];
                sharedFlux += beam.ReferenceFlux;
                if (HasRealPower(bg)) gdtfPower += bg.PowerConsumption;
                else allPowered = false;
            }
            if (power <= 0 && allPowered) power = gdtfPower;
            if (power <= 0 || !(fileMax > 0) || cd > fileTotal / 10 || cd < fileMax / 2 || cd > fileMax * 2) return null;
            var shares = new float[Beams.Count];
            double flux = 0;
            for (int b = 0; b < Beams.Count; b++)
            {
                shares[b] = (float)(fileCd[b] / fileMax);
                flux += Beams[b].ReferenceFlux * shares[b] / _peakShare[b];
            }
            double sharedEfficacy = sharedFlux / power, efficacy = flux / power;
            if (sharedEfficacy >= MinEfficacy || efficacy < MinEfficacy || efficacy > MaxEfficacy) return null;
            return (shares, fileMax, fileTotal, sharedEfficacy, efficacy);
        }

        /// <summary>
        /// Per beam: the Multiply relation masters that reach the beam through their followers and therefore must not
        /// scale it a second time as a beam dimmer. A colour master (a head colour scaling its pixels' colour) is
        /// excluded wherever its followers are. A dimmer master is excluded only where it scales all of the beam's light:
        /// when a dimmer that follows it (directly or along a chain) is applied to the beam, or when every colour channel
        /// of the beam follows it. Otherwise — e.g. a master dimmer whose relations reach only virtual head colours while
        /// addressed pixel colours light the beam (Cyclops CPX-412OB II, BeamZ StarColor 540) — it dims the beam directly.
        /// </summary>
        private List<int>?[] ExcludedRelationMasters()
        {
            var excluded = new List<int>?[Beams.Count];
            var followersOf = new Dictionary<int, List<int>>();
            foreach (int c in FollowerOrder)
            {
                foreach (int ri in FollowerRelations[c])
                {
                    var r = Relations[ri]; // relations dropped from a cycle do not reach the beam
                    if (r.Type != RelationType.Multiply) continue;
                    if (!followersOf.TryGetValue(r.Master, out var list)) followersOf[r.Master] = list = new List<int>();
                    if (!list.Contains(r.Follower)) list.Add(r.Follower);
                }
            }
            if (followersOf.Count == 0) return excluded;

            // the channels covering each beam, and which of them carry colour
            var beamChannels = new List<int>[Beams.Count];
            for (int b = 0; b < Beams.Count; b++) beamChannels[b] = new List<int>();
            var isColor = new bool[Channels.Count];
            foreach (var ch in Channels)
            {
                int first = _beamFirst[ch.Index], count = _beamCount[ch.Index];
                for (int b = first; b < first + count; b++) beamChannels[b].Add(ch.Index);
                for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                    if (Functions[k].Class.Kind is AttributeKind.ColorAdd or AttributeKind.ColorRgb or AttributeKind.HsbBrightness or AttributeKind.CieBrightness)
                        isColor[ch.Index] = true;
            }

            // masters with their followers first (chains: the pixel dimmer is decided before the grand master above it)
            var rank = new Dictionary<int, int>();
            for (int i = 0; i < FollowerOrder.Length; i++) rank[FollowerOrder[i]] = i;
            var masters = followersOf.Keys.OrderByDescending(m => rank.TryGetValue(m, out int r) ? r : -1).ThenBy(m => m).ToList();
            var isExcluded = new HashSet<(int Master, int Beam)>();
            foreach (int m in masters)
            {
                var reach = Reach(m, followersOf);
                var beams = new SortedSet<int>();
                foreach (int f in followersOf[m])
                    for (int b = _beamFirst[f]; b < _beamFirst[f] + _beamCount[f]; b++) beams.Add(b);
                bool dimmer = IsIntensityMaster(Channels[m]);
                foreach (int b in beams)
                {
                    bool exclude = !dimmer || ScalesWholeBeam(m, b, reach, beamChannels[b], isColor, isExcluded);
                    if (!exclude) continue;
                    isExcluded.Add((m, b));
                    var list = excluded[b] ??= new List<int>();
                    if (!list.Contains(m)) list.Add(m);
                }
            }
            return excluded;
        }

        /// <summary>True when dimmer master <paramref name="m"/> already scales all of beam <paramref name="b"/>'s light through its followers.</summary>
        private bool ScalesWholeBeam(int m, int b, HashSet<int> reach, List<int> channels, bool[] isColor, HashSet<(int, int)> isExcluded)
        {
            int colors = 0;
            bool allColorsFollow = true;
            foreach (int c in channels)
            {
                if (c == m) continue;
                if (reach.Contains(c) && IsIntensityMaster(Channels[c]) && !isExcluded.Contains((c, b))) return true;
                if (!isColor[c]) continue;
                colors++;
                allColorsFollow &= reach.Contains(c);
            }
            return colors > 0 && allColorsFollow;
        }

        /// <summary>Every channel that follows <paramref name="master"/> through Multiply relations, directly or along a chain.</summary>
        private static HashSet<int> Reach(int master, Dictionary<int, List<int>> followersOf)
        {
            var reach = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(master);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                if (!followersOf.TryGetValue(c, out var list)) continue;
                foreach (int f in list)
                    if (f != master && reach.Add(f)) stack.Push(f);
            }
            return reach;
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

        /// <summary>True when two angles in degrees agree within 0.5° or 2 %.</summary>
        private static bool SameAngle(float a, float b) => MathF.Abs(a - b) <= MathF.Max(0.5f, 0.02f * MathF.Max(a, b));

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

            // optics: native angle, field ratio, zoom range. An angle the file does not give is GDTF's placeholder; one it
            // gives as exactly GDTF's 25°/25° is most likely the editor's untouched default (GDTF Builder writes every
            // attribute), so _specs wins in both cases.
            float native = bg.BeamAngle;
            float field = bg.IsFieldAngleSpecified ? bg.FieldAngle : float.NaN;
            bool placeholderAngles = !bg.IsBeamAngleSpecified ||
                (native == GdtfDefaultAngle && (!bg.IsFieldAngleSpecified || bg.FieldAngle == GdtfDefaultAngle));
            if ((placeholderAngles || !(native > 0f)) && Specs?.BeamAngleDeg is { } specBeam && specBeam.Min > 0)
            {
                native = (float)specBeam.Min;
                field = Specs.FieldAngleDeg is { } sf && sf > native ? (float)sf : float.NaN;
            }
            if (!(native > 0f) || !float.IsFinite(native)) native = 25f;

            float zoomMin = float.PositiveInfinity, zoomMax = 0f;
            bool hasCmyOrCto = false, hasColorSub = false, hasColorTemperature = false, hasCtc = false, hasCrossfade = false, colourSystem = false;
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
                    colourSystem |= IsColourSource(f.Class.Kind) || f.Class.Kind is AttributeKind.ColorSub or AttributeKind.ColorMacro ||
                        f.Class.Wheel == WheelFamily.Color;
                }
            }
            bool hasZoom = zoomMax > 0f;
            beam.HasColorSystem = colourSystem;

            // field ratio: from the file's field angle unless that is not a field angle — the zoom range written into the
            // Beam's BeamAngle/FieldAngle (Ayrton Rivale Wash 4°/57°, Clay Paky Aleda B-EYE K10 4°/60° …: 53 beams in the
            // library) or a ratio no optic has (above MaxFieldRatio) — then the beam type's typical ratio
            float ratio = float.NaN;
            if (float.IsFinite(field) && field > native * 1.01f)
            {
                bool zoomRange = (hasZoom && SameAngle(native, zoomMin) && SameAngle(field, zoomMax)) ||
                    (Specs?.BeamAngleDeg is { } range && range.Max > range.Min && SameAngle(native, (float)range.Min) && SameAngle(field, (float)range.Max));
                float r = field / native;
                if (zoomRange)
                    Warnings.Add($"Beam '{beam.Name}': BeamAngle/FieldAngle {native:G4}°/{field:G4}° are the zoom range; the typical field ratio of a {beam.BeamType} beam is used.");
                else if (r > MaxFieldRatio)
                    Warnings.Add($"Beam '{beam.Name}': FieldAngle {field:G4}° is {r:G3} × the BeamAngle {native:G4}°; the typical field ratio of a {beam.BeamType} beam is used.");
                else
                    ratio = r;
            }
            beam.FieldRatioEstimated = float.IsNaN(ratio);
            if (float.IsNaN(ratio))
            {
                var est = BeamDistribution.Create(beam.BeamType, native * DegToRad, float.NaN);
                ratio = est.BeamAngle > 0f ? est.FieldAngle / est.BeamAngle : 1.3f;
            }
            beam.NativeBeamAngle = native * DegToRad;
            beam.FieldRatio = Math.Clamp(ratio, 1f, MaxFieldRatio);

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
                float perBeam = (float)(cd * _peakShare[beam.Index]);
                float specAngle = hasZoom ? zoomMin : native; // datasheet candela is the narrowest-zoom value
                beam.ReferenceFlux = perBeam * table.SolidAngle(specAngle * DegToRad);
                beam.PhotometrySource = PhotometrySource.SpecsCandela;
            }
            else if (Specs?.LuminousFluxLm is { } lm && lm > 0 && double.IsFinite(lm) && !HasRealFlux(bg))
            {
                // the GDTF flux is missing or GDTF's default: the researched fixture flux, shared between the beams
                beam.ReferenceFlux = (float)(lm * _beamShare[beam.Index]);
                beam.PhotometrySource = PhotometrySource.SpecsFlux;
            }
            else if (bg.IsLuminousFluxSpecified && bg.LuminousFlux > 0f && float.IsFinite(bg.LuminousFlux))
            {
                // GDTF's default (10 000 lm) on every pixel of a multi-beam fixture is not 10 000 lm per pixel: read it as
                // the fixture's total
                beam.ReferenceFlux = bg.LuminousFlux == GdtfDefaultFlux && Beams.Count > 1 ? bg.LuminousFlux / Beams.Count : bg.LuminousFlux;
                beam.PhotometrySource = PhotometrySource.GdtfFlux;
            }
            else if (HasRealPower(bg) || (Specs?.PowerConsumptionW is { } w && w > 0 && double.IsFinite(w)))
            {
                // no usable flux anywhere: the beam's (or the fixture's researched) electrical power × a typical efficacy
                float efficacy = EffectiveLampType(bg, emitterSet.Count > 0) switch
                {
                    LampType.Led => 40f,
                    LampType.Discharge => 45f,
                    LampType.Halogen => 18f,
                    _ => 15f,
                };
                float power = HasRealPower(bg) ? bg.PowerConsumption : (float)(Specs!.PowerConsumptionW!.Value * _beamShare[beam.Index]);
                beam.ReferenceFlux = power * efficacy;
                beam.PhotometrySource = PhotometrySource.Estimated;
            }
            else
            {
                // the file gives neither flux nor power: GDTF's default flux, read as the fixture's total
                beam.ReferenceFlux = GdtfDefaultFlux / Math.Max(1, Beams.Count);
                beam.PhotometrySource = PhotometrySource.Estimated;
            }

            // white source colour: the GDTF colour temperature, unless the file does not give it or gives GDTF's 6000 K
            // default (5900 of the library's 12 239 beams) and _specs knows the source: a single CCT, or the top of the
            // range of a source whose CTO/CMY filters work down from it
            float kelvin = bg.ColorTemperature;
            if ((!bg.IsColorTemperatureSpecified || kelvin == GdtfDefaultKelvin) && Specs?.ColorTemperatureK is { } specCct &&
                specCct.Max >= 1000 && specCct.Max <= 25000 && double.IsFinite(specCct.Max) &&
                (specCct.Max - specCct.Min <= 1.0 || hasCmyOrCto))
                kelvin = (float)specCct.Max;
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
                list = CalibrateWhitePoint(beam, list);
                beam.Emitters = list;
                beam.PresetOutput = BuildPresetOutput(beam, list);
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
        /// Colour wheels on an LED engine that mixes colour are presets, not filters: there is no dichroic in front of the
        /// emitters, the engine sets them to the preset's colour (Ayrton Zonda 3 FX "Color Preset": "SCARLET" x 0.448,
        /// y 0.332). Per colour wheel the beam uses, per slot the largest flux the emitters make at the slot's
        /// chromaticity relative to the beam's reference flux (<see cref="WhiteEfficiencyTable.MaxFlux"/>); NaN for open
        /// slots and for colours outside the emitters' gamut, which keep the filter model. Null for beams whose emitters
        /// do not mix colour.
        /// </summary>
        private float[]?[]? BuildPresetOutput(ProfileBeam beam, PhotometryEmitter[] list)
        {
            bool coloured = false;
            for (int i = 0; i < list.Length && !coloured; i++)
                for (int j = i + 1; j < list.Length && !coloured; j++)
                    coloured = Vector2.Distance(list[i].Chromaticity, list[j].Chromaticity) > 0.1f;
            if (!coloured || list.Length > 9) return null;
            float[]?[]? result = null;
            double[]? a = null, c = null, f = null;
            double total = 0;
            foreach (int ch in ChannelsOfBeam(beam))
            {
                var channel = Channels[ch];
                for (int k = channel.FirstFunction; k < channel.FirstFunction + channel.FunctionCount; k++)
                {
                    var fn = Functions[k];
                    if (fn.Class.Wheel != WheelFamily.Color || fn.Wheel < 0) continue;
                    result ??= new float[]?[Wheels.Count];
                    if (result[fn.Wheel] is not null) continue;
                    if (a is null)
                    {
                        a = new double[list.Length];
                        c = new double[list.Length];
                        f = new double[list.Length];
                        for (int e = 0; e < list.Length; e++)
                        {
                            a[e] = list[e].TristimulusPerLumen.X;
                            c[e] = list[e].TristimulusPerLumen.Z;
                            f[e] = list[e].MaxFlux;
                            total += f[e];
                        }
                    }
                    var wheel = Wheels[fn.Wheel];
                    var output = new float[wheel.SlotCount];
                    for (int s = 0; s < output.Length; s++)
                    {
                        var xyz = wheel.SlotColorXyzArray[s];
                        double flux = wheel.SlotOpenArray[s] || !(total > 0) || !(xyz.Y > 0f)
                            ? 0
                            : WhiteEfficiencyTable.MaxFlux(a, c!, f!, xyz.X / xyz.Y, xyz.Z / xyz.Y);
                        output[s] = flux > total * 1e-3 && double.IsFinite(flux) ? (float)(flux / total) : float.NaN;
                    }
                    result[fn.Wheel] = output;
                }
            }
            return result;
        }

        /// <summary>Largest distance of an LED engine's full white from the Planckian locus that is taken as written, Δuv.</summary>
        private const float MaxWhiteDuv = 0.02f;

        /// <summary>
        /// Checks the white an LED engine makes with all emitters at full against the fixture's researched colour
        /// temperature (<c>_specs</c> colorTemperatureK). Emitter outputs taken from a file can be far from photometric
        /// (Ayrton Zonda 3 FX: RGBW at full mixes to x 0.255, y 0.178, a violet 0.056 below the locus, where the datasheet
        /// gives 2700–8000 K); a real engine is calibrated to a white. When the full mix is more than
        /// <see cref="MaxWhiteDuv"/> off the locus or its CCT lies outside the range, the emitter outputs are trimmed as
        /// little as possible (<see cref="ColorMixing.TryBalance"/>) so that the full mix lands on the locus at the nearest
        /// CCT of the range, keeping the beam's reference flux and every emitter lit; the beam is marked
        /// <see cref="ProfileBeam.WhitePointEstimated"/> and a warning names the change. Engines with fewer than two
        /// differently coloured emitters, or without a researched range, are left alone.
        /// </summary>
        private PhotometryEmitter[] CalibrateWhitePoint(ProfileBeam beam, PhotometryEmitter[] list)
        {
            beam.WhitePointEstimated = false;
            beam.WhitePointKelvin = float.NaN;
            Vector3 sum = default;
            double total = 0;
            foreach (var e in list)
            {
                sum += e.TristimulusPerLumen * e.MaxFlux;
                total += e.MaxFlux;
            }
            float s = sum.X + sum.Y + sum.Z;
            if (!(s > 0f) || !(total > 0) || !float.IsFinite(s)) return list;
            var xy = new Vector2(sum.X / s, sum.Y / s);
            var cct = Cct.FromXy(xy);
            if (cct.IsMeaningful) beam.WhitePointKelvin = cct.Kelvin;
            if (Specs?.ColorTemperatureK is not { } range || !(range.Min >= 1000) || !(range.Max <= 25000) || range.Max < range.Min) return list;
            bool coloured = false;
            for (int i = 0; i < list.Length && !coloured; i++)
                for (int j = i + 1; j < list.Length && !coloured; j++)
                    coloured = Vector2.Distance(list[i].Chromaticity, list[j].Chromaticity) > 0.1f;
            if (!coloured) return list;
            float lo = (float)range.Min, hi = (float)range.Max;
            bool inRange = cct.IsMeaningful && MathF.Abs(cct.Duv) <= MaxWhiteDuv && cct.Kelvin >= lo / 1.02f && cct.Kelvin <= hi * 1.02f;
            if (inRange) return list;

            float target = cct.IsMeaningful && float.IsFinite(cct.Kelvin) ? Math.Clamp(cct.Kelvin, lo, hi) : hi;
            int n = list.Length;
            var factors = new float[n];
            if (n > 16 || !ColorMixing.TryBalance(list, Cct.XyFromCct(target), factors)) return list; // outside the emitters' gamut
            var calibrated = new PhotometryEmitter[n];
            for (int i = 0; i < n; i++) calibrated[i] = new PhotometryEmitter(list[i].Name, list[i].Chromaticity, list[i].MaxFlux * factors[i]);
            beam.WhitePointEstimated = true;
            beam.WhitePointKelvin = target;
            string key = string.Create(CultureInfo.InvariantCulture, $"{xy.X:F3},{xy.Y:F3}>{target:F0}");
            if (_whiteWarned.Add(key))
                Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Emitters at full mix to x {xy.X:F3}, y {xy.Y:F3} ({(cct.IsMeaningful ? $"{cct.Kelvin:F0} K, Δuv {cct.Duv:+0.000;-0.000}" : "no CCT")}), outside the datasheet's {lo:F0}–{hi:F0} K; emitter outputs re-balanced to {target:F0} K (estimate)."));
            return calibrated;
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
        /// Share of the fixture's researched flux (<c>_specs</c> flux or power) that each beam carries: proportional to
        /// the beams' GDTF LuminousFlux when the file gives them different values (a main engine and an aura ring, large
        /// and small cells), equal otherwise. A beam whose flux is missing or GDTF's default 10 000 lm counts as the mean
        /// of the beams with a real value. The shares of the datasheet peak start equal to these and follow the beams'
        /// optics once they are known (<see cref="PeakSharesByOptics"/>).
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
                var bg = Beams[b].Geometry;
                flux[b] = HasRealFlux(bg) ? bg.LuminousFlux : double.NaN;
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
            _peakShare = (float[])_beamShare.Clone();
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
