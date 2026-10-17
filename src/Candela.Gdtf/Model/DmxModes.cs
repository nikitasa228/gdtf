namespace Candela.Gdtf;

/// <summary>
/// A DMX mode: its root geometry, channels (with logical channels, channel functions and channel sets), relations and
/// macros, plus the resolved channel layout where channels of referenced geometries are instanced per
/// <see cref="GeometryReference"/> (see <see cref="ChannelInstances"/>).
/// </summary>
public sealed class DmxMode
{
    private Dictionary<string, DmxChannel>? _channelsByName;
    private Dictionary<string, ChannelFunction>? _functionsByName;

    /// <summary>Mode name (unique within the fixture type).</summary>
    public string Name { get; set; } = "";

    /// <summary>Description (GDTF 1.2).</summary>
    public string Description { get; set; } = "";

    /// <summary>Root geometry name as written.</summary>
    public string? GeometryName { get; set; }

    /// <summary>Resolved root geometry (a top-level geometry).</summary>
    public GdtfGeometry? Geometry { get; set; }

    /// <summary>Channels as written (templates for channels of referenced geometries).</summary>
    public List<DmxChannel> Channels { get; } = new();

    /// <summary>Master/follower relations between channels.</summary>
    public List<DmxRelation> Relations { get; } = new();

    /// <summary>Fixture type macros of this mode.</summary>
    public List<FtMacro> Macros { get; } = new();

    /// <summary>
    /// Every addressable channel occurrence: one per channel whose geometry is in the mode's own tree, and one per
    /// geometry reference for channels of referenced (template) geometries, with absolute offsets. Filled by the reader;
    /// recompute with <see cref="DmxLayout.Build(DmxMode)"/> after editing a mode in code.
    /// </summary>
    public IReadOnlyList<DmxChannelInstance> ChannelInstances { get; set; } = Array.Empty<DmxChannelInstance>();

    /// <summary>Number of DMX slots used per break (highest absolute offset of any channel instance in that break).</summary>
    public IReadOnlyDictionary<int, int> BreakFootprints { get; set; } = new Dictionary<int, int>();

    /// <summary>Total DMX footprint in slots (sum over breaks of <see cref="BreakFootprints"/>).</summary>
    public int Footprint
    {
        get
        {
            int sum = 0;
            foreach (var kv in BreakFootprints) sum += kv.Value;
            return sum;
        }
    }

    /// <summary>
    /// Highest offset written on the channels themselves, summed over breaks, ignoring geometry-reference instancing.
    /// This is the number simple tools (and <c>_specs/fixtures_index.json</c>) report; it is lower than
    /// <see cref="Footprint"/> for modes whose pixels are addressed through geometry references.
    /// </summary>
    public int DeclaredChannelCount
    {
        get
        {
            var perBreak = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var ch in Channels)
            {
                if (ch.Offsets.Length == 0) continue;
                string key = ch.BreakIsOverwrite ? "Overwrite" : ch.DmxBreak.ToString(System.Globalization.CultureInfo.InvariantCulture);
                int max = ch.Offsets.Max();
                perBreak[key] = System.Math.Max(perBreak.GetValueOrDefault(key), max);
            }
            return perBreak.Values.Sum();
        }
    }

    /// <summary>Channel by name (<c>Geometry_Attribute</c>), or null.</summary>
    public DmxChannel? FindChannel(string? name)
    {
        if (name is null) return null;
        var dict = _channelsByName;
        if (dict is null)
        {
            dict = new Dictionary<string, DmxChannel>(StringComparer.Ordinal);
            foreach (var c in Channels) dict.TryAdd(c.Name, c);
            _channelsByName = dict;
        }
        return dict.TryGetValue(name, out var ch) ? ch : null;
    }

    /// <summary>Channel function by full node name <c>Channel.LogicalChannel.Function</c>, or null.</summary>
    public ChannelFunction? FindFunction(string? fullName)
    {
        if (fullName is null) return null;
        var dict = _functionsByName;
        if (dict is null)
        {
            dict = new Dictionary<string, ChannelFunction>(StringComparer.Ordinal);
            foreach (var c in Channels)
                foreach (var f in c.Functions)
                    dict.TryAdd(f.FullName, f);
            _functionsByName = dict;
        }
        return dict.TryGetValue(fullName, out var fn) ? fn : null;
    }

    /// <summary>Drops the name lookup caches; call after editing channels in code.</summary>
    public void InvalidateLookups()
    {
        _channelsByName = null;
        _functionsByName = null;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// A DMX channel: break, offsets (coarse first) and the logical channel(s) it carries. Channels without offsets are
/// virtual (no DMX address, e.g. a virtual dimmer per pixel).
/// </summary>
public sealed class DmxChannel
{
    /// <summary>Owning mode (set by the reader).</summary>
    public DmxMode? Mode { get; set; }

    /// <summary>Channel name: <c>{Geometry}_{attribute of the first logical channel}</c> (GDTF naming rule).</summary>
    public string Name { get; set; } = "";

    /// <summary>1-based DMX break the channel is patched in (ignored when <see cref="BreakIsOverwrite"/>).</summary>
    public int DmxBreak { get; set; } = 1;

    /// <summary>True for DMXBreak "Overwrite": the break comes from the last Break of the geometry reference.</summary>
    public bool BreakIsOverwrite { get; set; }

    /// <summary>1-based offsets from most to least significant byte; empty for virtual channels.</summary>
    public int[] Offsets { get; set; } = Array.Empty<int>();

    /// <summary>True when the channel has no DMX address.</summary>
    public bool IsVirtual => Offsets.Length == 0;

    /// <summary>
    /// Resolution in bytes: the number of offsets, or for virtual channels the largest byte count of its DMX values.
    /// </summary>
    public int Resolution { get; set; } = 1;

    /// <summary>Largest DMX value of the channel in its resolution.</summary>
    public uint MaxValue => DmxValue.MaxFor(Resolution);

    /// <summary>Initial function node as written (<c>Channel.LogicalChannel.Function</c>), or null.</summary>
    public string? InitialFunctionName { get; set; }

    /// <summary>Resolved initial function (the one active after power-up).</summary>
    public ChannelFunction? InitialFunction { get; set; }

    /// <summary>Highlight value, or null ("None").</summary>
    public DmxValue? Highlight { get; set; }

    /// <summary>Geometry name as written.</summary>
    public string? GeometryName { get; set; }

    /// <summary>Resolved geometry the channel controls (may be inside a template geometry).</summary>
    public GdtfGeometry? Geometry { get; set; }

    /// <summary>Channel default as written by GDTF 1.0 on the DMXChannel element (later versions put it on functions).</summary>
    public DmxValue? LegacyDefault { get; set; }

    /// <summary>
    /// Default DMX value in channel resolution: GDTF 1.0 channel default, else the initial function's default, else the
    /// first function's default.
    /// </summary>
    public uint DefaultValue { get; set; }

    /// <summary>Logical channels (one in practice).</summary>
    public List<LogicalChannel> LogicalChannels { get; } = new();

    /// <summary>All channel functions of all logical channels in file order.</summary>
    public IEnumerable<ChannelFunction> Functions
    {
        get
        {
            foreach (var lc in LogicalChannels)
                foreach (var f in lc.Functions)
                    yield return f;
        }
    }

    /// <summary>Converts a DMX value to this channel's resolution.</summary>
    public uint ToChannelValue(DmxValue value) => value.ToResolution(Resolution);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Snap behaviour of a logical channel.</summary>
public enum SnapMode
{
    /// <summary>Values fade (default).</summary>
    No,

    /// <summary>Values jump.</summary>
    Yes,

    /// <summary>Snap on.</summary>
    On,

    /// <summary>Snap off.</summary>
    Off,
}

/// <summary>Which master (if any) scales a logical channel.</summary>
public enum ChannelMaster
{
    /// <summary>Not mastered.</summary>
    None,

    /// <summary>Scaled by the grand master.</summary>
    Grand,

    /// <summary>Scaled by a group master.</summary>
    Group,
}

/// <summary>A logical channel: the attribute a DMX channel controls and its channel functions.</summary>
public sealed class LogicalChannel
{
    /// <summary>Owning DMX channel (set by the reader).</summary>
    public DmxChannel? Channel { get; set; }

    /// <summary>Attribute node as written.</summary>
    public string? AttributeName { get; set; }

    /// <summary>Resolved attribute.</summary>
    public GdtfAttribute? Attribute { get; set; }

    /// <summary>Name used in node paths (the attribute name).</summary>
    public string Name => AttributeName ?? "";

    /// <summary>Snap behaviour.</summary>
    public SnapMode Snap { get; set; }

    /// <summary>Master.</summary>
    public ChannelMaster Master { get; set; }

    /// <summary>Move-in-black fade time in s.</summary>
    public float MibFade { get; set; }

    /// <summary>Minimum time between DMX changes in s (0 = unlimited).</summary>
    public float DmxChangeTimeLimit { get; set; }

    /// <summary>Channel functions in file order.</summary>
    public List<ChannelFunction> Functions { get; } = new();
}

/// <summary>
/// A channel function: a DMX range of a channel mapped to a physical range of an attribute, optionally tied to a wheel,
/// emitter, filter, colour space, gamut or DMX profile, and optionally active only while another channel (the mode
/// master) is within <see cref="ModeFrom"/> … <see cref="ModeTo"/>.
/// </summary>
public sealed class ChannelFunction
{
    /// <summary>Owning logical channel (set by the reader).</summary>
    public LogicalChannel? LogicalChannel { get; set; }

    /// <summary>Name; GDTF default <c>"{Attribute} {n}"</c> (n = 1-based index in the logical channel) when empty.</summary>
    public string Name { get; set; } = "";

    /// <summary>1-based index in the logical channel.</summary>
    public int Index { get; set; }

    /// <summary>Node path <c>Channel.LogicalChannel.Function</c> used by references.</summary>
    public string FullName => $"{LogicalChannel?.Channel?.Name}.{LogicalChannel?.Name}.{Name}";

    /// <summary>Attribute node as written (GDTF default "NoFeature").</summary>
    public string? AttributeName { get; set; }

    /// <summary>Resolved attribute (null for NoFeature).</summary>
    public GdtfAttribute? Attribute { get; set; }

    /// <summary>Manufacturer's original attribute name.</summary>
    public string? OriginalAttribute { get; set; }

    /// <summary>Start of the DMX range as written.</summary>
    public DmxValue DmxFrom { get; set; } = DmxValue.Zero;

    /// <summary>Start of the DMX range in channel resolution.</summary>
    public uint DmxFromValue { get; set; }

    /// <summary>End of the DMX range in channel resolution (next function's start − 1 within the same mode-master context, or the channel maximum).</summary>
    public uint DmxToValue { get; set; }

    /// <summary>Default value as written (GDTF 1.1+; 0/1 when absent).</summary>
    public DmxValue Default { get; set; } = DmxValue.Zero;

    /// <summary>Physical value at <see cref="DmxFromValue"/> in the attribute's unit (degrees for angles, GDTF default 0).</summary>
    public float PhysicalFrom { get; set; }

    /// <summary>Physical value at <see cref="DmxToValue"/> (GDTF default 1).</summary>
    public float PhysicalTo { get; set; } = 1f;

    /// <summary>Time in s to move from PhysicalFrom to PhysicalTo at full speed.</summary>
    public float RealFade { get; set; }

    /// <summary>Time in s to accelerate to full speed.</summary>
    public float RealAcceleration { get; set; }

    /// <summary>Wheel name as written.</summary>
    public string? WheelName { get; set; }

    /// <summary>Resolved wheel.</summary>
    public Wheel? Wheel { get; set; }

    /// <summary>Emitter name as written.</summary>
    public string? EmitterName { get; set; }

    /// <summary>Resolved emitter.</summary>
    public Emitter? Emitter { get; set; }

    /// <summary>Filter name as written.</summary>
    public string? FilterName { get; set; }

    /// <summary>Resolved filter.</summary>
    public Filter? Filter { get; set; }

    /// <summary>Colour space name as written (GDTF 1.2).</summary>
    public string? ColorSpaceName { get; set; }

    /// <summary>Resolved colour space.</summary>
    public ColorSpace? ColorSpace { get; set; }

    /// <summary>Gamut name as written (GDTF 1.2).</summary>
    public string? GamutName { get; set; }

    /// <summary>Resolved gamut.</summary>
    public Gamut? Gamut { get; set; }

    /// <summary>Mode master node as written: a channel name or a function node path.</summary>
    public string? ModeMasterName { get; set; }

    /// <summary>Resolved mode master channel (also set when the master is a function: its channel).</summary>
    public DmxChannel? ModeMasterChannel { get; set; }

    /// <summary>Resolved mode master function, when the master is a function.</summary>
    public ChannelFunction? ModeMasterFunction { get; set; }

    /// <summary>Start of the master's DMX range in which this function is active.</summary>
    public DmxValue ModeFrom { get; set; } = DmxValue.Zero;

    /// <summary>End of the master's DMX range in which this function is active.</summary>
    public DmxValue ModeTo { get; set; } = DmxValue.Zero;

    /// <summary>DMX profile name as written (GDTF 1.2).</summary>
    public string? DmxProfileName { get; set; }

    /// <summary>Resolved DMX profile.</summary>
    public DmxProfile? DmxProfile { get; set; }

    /// <summary>Minimum physical value the function reaches (GDTF 1.2; defaults to PhysicalFrom).</summary>
    public float Min { get; set; }

    /// <summary>Maximum physical value the function reaches (GDTF 1.2; defaults to PhysicalTo).</summary>
    public float Max { get; set; } = 1f;

    /// <summary>Custom display name (GDTF 1.2).</summary>
    public string? CustomName { get; set; }

    /// <summary>Named DMX sub-ranges.</summary>
    public List<ChannelSet> ChannelSets { get; } = new();

    /// <summary>Sub channel sets (GDTF 1.2).</summary>
    public List<SubChannelSet> SubChannelSets { get; } = new();

    /// <summary>True when the function depends on a mode master.</summary>
    public bool HasModeMaster => ModeMasterName is not null;

    /// <summary>Physical value for a DMX value inside this function's range (linear interpolation, clamped).</summary>
    public float PhysicalAt(uint dmxValue)
    {
        if (DmxToValue <= DmxFromValue) return PhysicalFrom;
        double t = System.Math.Clamp(((double)dmxValue - DmxFromValue) / ((double)DmxToValue - DmxFromValue), 0.0, 1.0);
        return (float)(PhysicalFrom + (PhysicalTo - PhysicalFrom) * t);
    }

    /// <inheritdoc />
    public override string ToString() => FullName;
}

/// <summary>A named DMX sub-range of a channel function (e.g. a gobo slot or "Open").</summary>
public sealed class ChannelSet
{
    /// <summary>Owning function (set by the reader).</summary>
    public ChannelFunction? Function { get; set; }

    /// <summary>Name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Start of the range as written.</summary>
    public DmxValue DmxFrom { get; set; } = DmxValue.Zero;

    /// <summary>Start of the range in channel resolution.</summary>
    public uint DmxFromValue { get; set; }

    /// <summary>End of the range in channel resolution (next set's start − 1, or the function's end).</summary>
    public uint DmxToValue { get; set; }

    /// <summary>Physical start value, or null when the set inherits the function's interpolation.</summary>
    public float? PhysicalFrom { get; set; }

    /// <summary>Physical end value, or null.</summary>
    public float? PhysicalTo { get; set; }

    /// <summary>1-based slot of the function's wheel, 0 when the set does not select a slot.</summary>
    public int WheelSlotIndex { get; set; }

    /// <summary>Resolved wheel slot.</summary>
    public WheelSlot? WheelSlot { get; set; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A sub channel set (GDTF 1.2): a physical sub-range tied to a sub-physical unit.</summary>
public sealed class SubChannelSet
{
    /// <summary>Name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Physical start value.</summary>
    public float PhysicalFrom { get; set; }

    /// <summary>Physical end value.</summary>
    public float PhysicalTo { get; set; }

    /// <summary>Sub-physical unit node as written.</summary>
    public string? SubPhysicalUnitName { get; set; }

    /// <summary>Resolved sub-physical unit.</summary>
    public SubPhysicalUnit? SubPhysicalUnit { get; set; }

    /// <summary>DMX profile name as written.</summary>
    public string? DmxProfileName { get; set; }

    /// <summary>Resolved DMX profile.</summary>
    public DmxProfile? DmxProfile { get; set; }
}

/// <summary>How a relation's master affects the follower.</summary>
public enum RelationType
{
    /// <summary>Follower output is multiplied by the master (e.g. virtual dimmer).</summary>
    Multiply,

    /// <summary>Master overrides the follower.</summary>
    Override,
}

/// <summary>A master/follower relation between a channel and a channel function.</summary>
public sealed class DmxRelation
{
    /// <summary>Name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Master channel name as written.</summary>
    public string? MasterName { get; set; }

    /// <summary>Resolved master channel.</summary>
    public DmxChannel? Master { get; set; }

    /// <summary>Follower function node as written.</summary>
    public string? FollowerName { get; set; }

    /// <summary>Resolved follower function.</summary>
    public ChannelFunction? Follower { get; set; }

    /// <summary>Relation type.</summary>
    public RelationType Type { get; set; }
}

/// <summary>A fixture type macro: a sequence of DMX steps, optionally bound to a channel function.</summary>
public sealed class FtMacro
{
    /// <summary>Name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Channel function node as written.</summary>
    public string? ChannelFunctionName { get; set; }

    /// <summary>Resolved channel function.</summary>
    public ChannelFunction? ChannelFunction { get; set; }

    /// <summary>DMX steps in order.</summary>
    public List<MacroDmxStep> Steps { get; } = new();
}

/// <summary>One step of a macro.</summary>
public sealed class MacroDmxStep
{
    /// <summary>Duration in s (GDTF default 1).</summary>
    public float Duration { get; set; } = 1f;

    /// <summary>Channel values of the step.</summary>
    public List<MacroDmxValue> Values { get; } = new();
}

/// <summary>A channel value of a macro step.</summary>
public sealed class MacroDmxValue
{
    /// <summary>Value.</summary>
    public DmxValue Value { get; set; } = DmxValue.Zero;

    /// <summary>Channel name as written.</summary>
    public string? DmxChannelName { get; set; }

    /// <summary>Resolved channel.</summary>
    public DmxChannel? DmxChannel { get; set; }
}

/// <summary>
/// One addressable occurrence of a <see cref="DmxChannel"/> in a mode. Channels of the mode's own geometry tree have one
/// instance with the offsets as written; channels of a referenced geometry have one instance per
/// <see cref="GeometryReference"/> with absolute offsets <c>offset + Break.DMXOffset − 1</c>.
/// </summary>
public sealed class DmxChannelInstance
{
    /// <summary>Creates an instance.</summary>
    public DmxChannelInstance(DmxChannel channel, int dmxBreak, int[] offsets, GdtfGeometry? geometry, IReadOnlyList<GeometryReference> references)
    {
        Channel = channel;
        Break = dmxBreak;
        Offsets = offsets;
        Geometry = geometry;
        References = references;
    }

    /// <summary>The channel definition (template).</summary>
    public DmxChannel Channel { get; }

    /// <summary>1-based DMX break of this occurrence.</summary>
    public int Break { get; }

    /// <summary>Absolute 1-based offsets within the break (coarse first); empty for virtual channels.</summary>
    public int[] Offsets { get; }

    /// <summary>Geometry node the channel controls (inside a template for referenced channels); null when unresolved.</summary>
    public GdtfGeometry? Geometry { get; }

    /// <summary>Chain of geometry references from the mode root down to this instance (outermost first); empty for direct channels.</summary>
    public IReadOnlyList<GeometryReference> References { get; }

    /// <summary>The innermost reference, or null for direct channels.</summary>
    public GeometryReference? Reference => References.Count > 0 ? References[^1] : null;

    /// <summary>Readable instance name, e.g. <c>Pixel 3</c> or <c>Head</c>.</summary>
    public string InstanceName => Reference?.Name ?? Geometry?.Name ?? Channel.GeometryName ?? "";

    /// <summary>First (coarse) absolute offset, or 0 for virtual channels.</summary>
    public int FirstOffset => Offsets.Length > 0 ? Offsets[0] : 0;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Channel.Name} @ {InstanceName} [{Break}:{string.Join(",", Offsets)}]";
}
