namespace Candela.Gdtf;

/// <summary>
/// Turns the names written in a GDTF file into object links after parsing, tolerating dangling references (they stay
/// null and produce a warning), and computes per-channel resolution, DMX ranges and defaults.
/// </summary>
internal static class Resolver
{
    public static void Resolve(ReadContext ctx)
    {
        var ft = ctx.FixtureType;
        var geometryIndex = BuildGeometryIndex(ctx);
        ResolveAttributes(ctx);
        ResolveWheels(ctx);
        ResolveGeometries(ctx, geometryIndex);
        ResolveProperties(ctx, geometryIndex);
        // The lookups every mode uses are built once: rebuilding them per mode costs modes × definitions, which a
        // hostile file with thousands of both turns into minutes.
        var names = new DefinitionIndex(ft);
        foreach (var mode in ft.DmxModes)
        {
            try
            {
                ResolveMode(ctx, mode, geometryIndex, names);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ctx.Warn($"DMXModes/{mode.Name}", $"References could not be resolved completely ({e.GetType().Name}: {e.Message}).");
            }
        }
        var modes = new ModeIndex(ft.DmxModes);
        foreach (var rdm in ft.Protocols.Rdm)
            foreach (var sv in rdm.SoftwareVersions)
                foreach (var p in sv.Personalities)
                {
                    p.DmxMode = modes.Find(p.DmxModeName);
                    if (p.DmxMode is null && !string.IsNullOrEmpty(p.DmxModeName))
                        ctx.Warn("Protocols/FTRDM", $"DMX personality refers to unknown mode '{p.DmxModeName}'.");
                }
        ft.InvalidateLookups();
    }

    /// <summary>
    /// Name lookups of the definitions channel functions refer to, shared by all modes of a fixture type.
    /// </summary>
    private sealed class DefinitionIndex
    {
        public DefinitionIndex(FixtureType ft)
        {
            var pd = ft.PhysicalDescriptions;
            Attributes = new NameIndex<GdtfAttribute>(ft.AttributeDefinitions.Attributes, a => a.Name);
            Wheels = new NameIndex<Wheel>(ft.Wheels, w => w.Name);
            Emitters = new NameIndex<Emitter>(pd.Emitters, e => e.Name);
            Filters = new NameIndex<Filter>(pd.Filters, f => f.Name);
            Gamuts = new NameIndex<Gamut>(pd.Gamuts, g => g.Name);
            Profiles = new NameIndex<DmxProfile>(pd.DmxProfiles, p => p.Name);
            ColorSpaces = new NameIndex<ColorSpace>(pd.AdditionalColorSpaces.Prepend(pd.ColorSpace), c => c.Name);
        }

        public NameIndex<GdtfAttribute> Attributes { get; }
        public NameIndex<Wheel> Wheels { get; }
        public NameIndex<Emitter> Emitters { get; }
        public NameIndex<Filter> Filters { get; }
        public NameIndex<Gamut> Gamuts { get; }
        public NameIndex<DmxProfile> Profiles { get; }
        public NameIndex<ColorSpace> ColorSpaces { get; }
    }

    /// <summary>
    /// Mode lookup with the semantics of <see cref="FixtureType.FindMode"/> (empty name: the first mode; exact name, then
    /// trimmed and case-insensitive), indexed so that resolving many personalities stays linear.
    /// </summary>
    private sealed class ModeIndex
    {
        private readonly List<DmxMode> _modes;
        private readonly Dictionary<string, DmxMode> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DmxMode> _loose = new(StringComparer.OrdinalIgnoreCase);

        public ModeIndex(List<DmxMode> modes)
        {
            _modes = modes;
            foreach (var m in modes)
            {
                _exact.TryAdd(m.Name, m);
                _loose.TryAdd(m.Name.Trim(), m);
            }
        }

        public DmxMode? Find(string? name)
        {
            if (string.IsNullOrEmpty(name)) return _modes.Count > 0 ? _modes[0] : null;
            if (_exact.TryGetValue(name, out var m)) return m;
            return _loose.TryGetValue(name.Trim(), out m) ? m : null;
        }
    }

    // ---------------------------------------------------------------- generic tolerant lookup

    /// <summary>Exact match first, then trimmed case-insensitive, then the last segment of a dotted node name.</summary>
    private sealed class NameIndex<T> where T : class
    {
        private readonly Dictionary<string, T> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, T> _loose = new(StringComparer.OrdinalIgnoreCase);

        public NameIndex(IEnumerable<T> items, Func<T, string> name)
        {
            foreach (var item in items)
            {
                var n = name(item);
                _exact.TryAdd(n, item);
                _loose.TryAdd(n.Trim(), item);
            }
        }

        public T? Find(string? name)
        {
            if (name is null) return null;
            if (_exact.TryGetValue(name, out var hit)) return hit;
            if (_loose.TryGetValue(name.Trim(), out hit)) return hit;
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1 && _loose.TryGetValue(name[(dot + 1)..].Trim(), out hit)) return hit;
            return null;
        }
    }

    // ---------------------------------------------------------------- attributes and wheels

    private static void ResolveAttributes(ReadContext ctx)
    {
        var defs = ctx.FixtureType.AttributeDefinitions;
        var groups = new NameIndex<ActivationGroup>(defs.ActivationGroups, g => g.Name);
        var attributes = new NameIndex<GdtfAttribute>(defs.Attributes, a => a.Name);
        var features = new FeatureIndex(defs);

        foreach (var a in defs.Attributes)
        {
            string path = ReadContext.ChildPath("Attributes", a.Name);
            if (a.ActivationGroupName is not null)
            {
                a.ActivationGroup = groups.Find(a.ActivationGroupName);
                if (a.ActivationGroup is null) ctx.Warn(path, $"Activation group '{a.ActivationGroupName}' is not defined.");
                else a.ActivationGroup.Attributes.Add(a);
            }
            if (a.FeatureName is not null)
            {
                a.Feature = features.Find(a.FeatureName);
                if (a.Feature is null) ctx.Warn(path, $"Feature '{a.FeatureName}' is not defined.");
            }
            if (a.MainAttributeName is not null)
            {
                a.MainAttribute = attributes.Find(a.MainAttributeName);
                if (a.MainAttribute is null) ctx.Warn(path, $"Main attribute '{a.MainAttributeName}' is not defined.");
            }
        }
    }

    /// <summary>
    /// Feature lookup by node <c>Group.Feature</c>: the feature of that group (trimmed, case-insensitive), else the first
    /// feature of any group with the name after the first dot. Indexed, so resolving many attributes stays linear.
    /// </summary>
    private sealed class FeatureIndex
    {
        private readonly NameIndex<FeatureGroup> _groups;
        private readonly Dictionary<FeatureGroup, Dictionary<string, Feature>> _byGroup = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, Feature> _any = new(StringComparer.OrdinalIgnoreCase);

        public FeatureIndex(AttributeDefinitions defs)
        {
            _groups = new NameIndex<FeatureGroup>(defs.FeatureGroups, g => g.Name);
            foreach (var g in defs.FeatureGroups)
            {
                var own = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in g.Features)
                {
                    own.TryAdd(f.Name.Trim(), f);
                    _any.TryAdd(f.Name.Trim(), f);
                }
                _byGroup.TryAdd(g, own);
            }
        }

        public Feature? Find(string node)
        {
            int dot = node.IndexOf('.');
            string last = dot >= 0 ? node[(dot + 1)..].Trim() : node.Trim();
            if (dot > 0 && _groups.Find(node[..dot]) is { } group &&
                _byGroup.TryGetValue(group, out var own) && own.TryGetValue(last, out var hit))
                return hit;
            return _any.TryGetValue(last, out hit) ? hit : null;
        }
    }

    private static void ResolveWheels(ReadContext ctx)
    {
        var filters = new NameIndex<Filter>(ctx.FixtureType.PhysicalDescriptions.Filters, f => f.Name);
        foreach (var w in ctx.FixtureType.Wheels)
            foreach (var s in w.Slots)
            {
                if (s.FilterName is null) continue;
                s.Filter = filters.Find(s.FilterName);
                if (s.Filter is null) ctx.Warn($"Wheels/{w.Name}/{s.Name}", $"Filter '{s.FilterName}' is not defined.");
            }
    }

    // ---------------------------------------------------------------- geometries

    /// <summary>All geometries by exact name (duplicates kept in tree order) plus a loose fallback.</summary>
    private sealed class GeometryIndex
    {
        public Dictionary<string, List<GdtfGeometry>> ByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<GdtfGeometry>> ByLooseName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public NameIndex<GdtfGeometry> TopLevel { get; init; } = null!;
        public NameIndex<WiringObjectGeometry> WiringObjects { get; init; } = null!;

        /// <summary>Picks per (mode root, name): files repeat geometry names, and channels repeat geometry names.</summary>
        private readonly Dictionary<(GdtfGeometry? Root, string Name), GdtfGeometry?> _picked = new();

        /// <summary>For a repeated name: its candidates per top-level tree, in tree order (built on first use).</summary>
        private readonly Dictionary<IReadOnlyList<GdtfGeometry>, Dictionary<GdtfGeometry, List<GdtfGeometry>>> _byTree =
            new(ReferenceEqualityComparer.Instance);

        public IReadOnlyList<GdtfGeometry> Find(string name)
        {
            if (ByName.TryGetValue(name, out var list)) return list;
            if (ByLooseName.TryGetValue(name.Trim(), out list)) return list;
            return Array.Empty<GdtfGeometry>();
        }

        /// <summary>
        /// The geometry a channel of a mode with root <paramref name="modeRoot"/> means by <paramref name="name"/>: among
        /// geometries with that name the first one in the mode's tree, else the first one. Only the candidates in the
        /// root's own top-level tree are tested, so a name repeated thousands of times costs no scan per channel.
        /// </summary>
        public GdtfGeometry? Pick(string name, GdtfGeometry? modeRoot)
        {
            if (_picked.TryGetValue((modeRoot, name), out var picked)) return picked;
            var candidates = Find(name);
            picked = candidates.Count switch
            {
                0 => null,
                1 => candidates[0],
                _ when modeRoot is null => candidates[0],
                _ => FirstInTree(candidates, modeRoot) ?? candidates[0],
            };
            _picked[(modeRoot, name)] = picked;
            return picked;
        }

        private GdtfGeometry? FirstInTree(IReadOnlyList<GdtfGeometry> candidates, GdtfGeometry modeRoot)
        {
            if (!_byTree.TryGetValue(candidates, out var trees))
            {
                trees = new Dictionary<GdtfGeometry, List<GdtfGeometry>>(ReferenceEqualityComparer.Instance);
                foreach (var g in candidates)
                {
                    var top = g.TopLevel;
                    if (!trees.TryGetValue(top, out var inTree)) trees[top] = inTree = new List<GdtfGeometry>(1);
                    inTree.Add(g);
                }
                _byTree[candidates] = trees;
            }
            if (!trees.TryGetValue(modeRoot.TopLevel, out var sameTree)) return null;
            if (modeRoot.Parent is null) return sameTree[0]; // a top-level root contains its whole tree
            foreach (var g in sameTree)
                if (g.IsSelfOrDescendantOf(modeRoot)) return g;
            return null;
        }
    }

    private static GeometryIndex BuildGeometryIndex(ReadContext ctx)
    {
        var ft = ctx.FixtureType;
        var all = ft.AllGeometries().ToList();
        var index = new GeometryIndex
        {
            TopLevel = new NameIndex<GdtfGeometry>(ft.Geometries, g => g.Name),
            WiringObjects = new NameIndex<WiringObjectGeometry>(all.OfType<WiringObjectGeometry>(), w => w.Name),
        };
        foreach (var g in all)
        {
            if (!index.ByName.TryGetValue(g.Name, out var list)) index.ByName[g.Name] = list = new List<GdtfGeometry>(1);
            list.Add(g);
            if (!index.ByLooseName.TryGetValue(g.Name.Trim(), out var loose)) index.ByLooseName[g.Name.Trim()] = loose = new List<GdtfGeometry>(1);
            loose.Add(g);
        }
        foreach (var kv in index.ByName)
            if (kv.Value.Count > 1)
                ctx.Warn("Geometries", $"Geometry name '{kv.Key}' is used {kv.Value.Count} times; references use the first one.");
        return index;
    }

    private static void ResolveGeometries(ReadContext ctx, GeometryIndex index)
    {
        var ft = ctx.FixtureType;
        var models = new NameIndex<GdtfModel>(ft.Models, m => m.Name);
        var emitters = new NameIndex<Emitter>(ft.PhysicalDescriptions.Emitters, e => e.Name);

        foreach (var g in ft.AllGeometries())
        {
            string path = ReadContext.ChildPath("Geometries", g.Name);
            if (g.ModelName is not null)
            {
                g.Model = models.Find(g.ModelName);
                if (g.Model is null) ctx.Warn(path, $"Model '{g.ModelName}' is not defined.");
            }
            switch (g)
            {
                case GeometryReference r:
                    ResolveReference(ctx, r, index, path);
                    break;
                case BeamGeometry b when b.EmitterSpectrumName is not null:
                    b.EmitterSpectrum = emitters.Find(b.EmitterSpectrumName);
                    if (b.EmitterSpectrum is null) ctx.Warn(path, $"Emitter spectrum '{b.EmitterSpectrumName}' is not defined.");
                    break;
                case LaserGeometry l when l.EmitterName is not null:
                    l.Emitter = emitters.Find(l.EmitterName);
                    if (l.Emitter is null) ctx.Warn(path, $"Emitter '{l.EmitterName}' is not defined.");
                    break;
                case StructureGeometry s when s.LinkedGeometryName is not null:
                    var linked = index.Find(s.LinkedGeometryName);
                    s.LinkedGeometry = linked.Count > 0 ? linked[0] : null;
                    if (s.LinkedGeometry is null) ctx.Warn(path, $"Linked geometry '{s.LinkedGeometryName}' is not defined.");
                    break;
                case WiringObjectGeometry w:
                    foreach (var pp in w.PinPatches)
                    {
                        if (pp.ToWiringObjectName is null) continue;
                        pp.ToWiringObject = index.WiringObjects.Find(pp.ToWiringObjectName);
                        if (pp.ToWiringObject is null) ctx.Warn(path, $"Pin patch target '{pp.ToWiringObjectName}' is not a wiring object.");
                    }
                    break;
            }
        }
    }

    private static void ResolveReference(ReadContext ctx, GeometryReference r, GeometryIndex index, string path)
    {
        if (r.GeometryName is null)
        {
            ctx.Warn(path, "GeometryReference has no Geometry attribute.");
            return;
        }
        var target = index.TopLevel.Find(r.GeometryName);
        if (target is null)
        {
            var any = index.Find(r.GeometryName);
            if (any.Count > 0)
            {
                target = any[0];
                ctx.Warn(path, $"Referenced geometry '{r.GeometryName}' is not a top-level geometry; used anyway.");
            }
        }
        if (target is null)
        {
            ctx.Warn(path, $"Referenced geometry '{r.GeometryName}' is not defined.");
            return;
        }
        if (r.IsSelfOrDescendantOf(target))
        {
            ctx.Warn(path, $"Geometry reference to '{r.GeometryName}' would contain itself; ignored.");
            return;
        }
        r.ReferencedGeometry = target;
    }

    private static void ResolveProperties(ReadContext ctx, GeometryIndex index)
    {
        var pd = ctx.FixtureType.PhysicalDescriptions;
        NameIndex<Connector>? legacy = null;
        foreach (var pc in pd.Properties.PowerConsumptions)
        {
            if (pc.ConnectorName is null) continue;
            pc.Connector = index.WiringObjects.Find(pc.ConnectorName);
            if (pc.Connector is not null) continue;
            // Files without wiring objects (most GDTF 1.2 files of the library) name a legacy <Connector> instead.
            legacy ??= new NameIndex<Connector>(pd.Connectors, c => c.Name);
            pc.LegacyConnector = legacy.Find(pc.ConnectorName);
            if (pc.LegacyConnector is null)
                ctx.Warn("Properties/PowerConsumption", $"Connector '{pc.ConnectorName}' is neither a wiring object nor a connector.");
        }
    }

    // ---------------------------------------------------------------- DMX modes

    private static void ResolveMode(ReadContext ctx, DmxMode mode, GeometryIndex index, DefinitionIndex names)
    {
        var ft = ctx.FixtureType;
        string modePath = ReadContext.ChildPath("DMXModes", mode.Name);

        // Root geometry.
        if (mode.GeometryName is not null)
        {
            mode.Geometry = index.TopLevel.Find(mode.GeometryName);
            if (mode.Geometry is null)
            {
                var any = index.Find(mode.GeometryName);
                if (any.Count > 0)
                {
                    mode.Geometry = any[0];
                    ctx.Warn(modePath, $"Mode geometry '{mode.GeometryName}' is not top-level; used anyway.");
                }
            }
        }
        if (mode.Geometry is null && ft.Geometries.Count > 0)
        {
            mode.Geometry = ft.Geometries[0];
            ctx.Warn(modePath, mode.GeometryName is null
                ? $"Mode has no geometry; '{mode.Geometry.Name}' used."
                : $"Mode geometry '{mode.GeometryName}' is not defined; '{mode.Geometry.Name}' used.");
        }

        foreach (var ch in mode.Channels)
        {
            string path = ReadContext.ChildPath(modePath, ch.Name);
            if (ch.GeometryName is not null)
            {
                ch.Geometry = index.Pick(ch.GeometryName, mode.Geometry);
                if (ch.Geometry is null) ctx.Warn(path, $"Geometry '{ch.GeometryName}' is not defined.");
            }
            else
            {
                ctx.Warn(path, "DMX channel has no geometry.");
            }

            foreach (var lc in ch.LogicalChannels)
            {
                if (lc.AttributeName is not null && !IsNoFeature(lc.AttributeName))
                {
                    lc.Attribute = names.Attributes.Find(lc.AttributeName);
                    if (lc.Attribute is null) ctx.Warn(path, $"Attribute '{lc.AttributeName}' is not defined.");
                }
                foreach (var f in lc.Functions)
                    ResolveFunction(ctx, f, path, names);
            }
            ch.Resolution = ch.Offsets.Length > 0 ? ch.Offsets.Length : VirtualResolution(ch);
        }

        // Node references between channels and functions (need every channel's name and resolution first).
        var nodes = new ModeNodeIndex(mode);
        foreach (var ch in mode.Channels)
        {
            string path = ReadContext.ChildPath(modePath, ch.Name);
            if (ch.InitialFunctionName is not null)
            {
                ch.InitialFunction = nodes.FindOwnFunction(ch, ch.InitialFunctionName);
                if (ch.InitialFunction is null)
                    ctx.Warn(path, $"Initial function '{ch.InitialFunctionName}' is not a function of this channel; first function used.");
            }
            ch.InitialFunction ??= ch.Functions.FirstOrDefault();
            ch.DefaultValue = ch.LegacyDefault is { } legacy
                ? ch.ToChannelValue(legacy)
                : ch.InitialFunction is { } init ? ch.ToChannelValue(init.Default) : 0u;

            foreach (var f in ch.Functions)
            {
                if (f.ModeMasterName is null) continue;
                var (masterChannel, masterFunction) = nodes.Find(f.ModeMasterName);
                f.ModeMasterChannel = masterChannel;
                f.ModeMasterFunction = masterFunction;
                if (masterChannel is null)
                {
                    ctx.Warn(path, $"Mode master '{f.ModeMasterName}' is not a channel or function of this mode.");
                    continue;
                }
                f.ModeFromValue = f.ModeFrom.ToResolution(masterChannel.Resolution);
                f.ModeToValue = f.ModeTo.ToRangeEnd(masterChannel.Resolution);
                if (f.ModeToValue < f.ModeFromValue)
                {
                    ctx.Warn(path, $"Function '{f.Name}': ModeTo {f.ModeTo} is below ModeFrom {f.ModeFrom}; the range is swapped.");
                    (f.ModeFromValue, f.ModeToValue) = (f.ModeTo.ToResolution(masterChannel.Resolution), f.ModeFrom.ToRangeEnd(masterChannel.Resolution));
                }
            }
        }

        // DMX ranges need the mode masters: functions that cannot be active together do not end each other's range.
        foreach (var ch in mode.Channels) ComputeRanges(ch, ctx.Warnings, ReadContext.ChildPath(modePath, ch.Name));

        foreach (var rel in mode.Relations)
        {
            string path = ReadContext.ChildPath(modePath + "/Relations", rel.Name);
            if (rel.MasterName is not null)
            {
                rel.Master = nodes.Find(rel.MasterName).Channel;
                if (rel.Master is null) ctx.Warn(path, $"Master '{rel.MasterName}' is not a channel of this mode.");
            }
            if (rel.FollowerName is not null)
            {
                rel.Follower = nodes.Find(rel.FollowerName).Function;
                if (rel.Follower is null) ctx.Warn(path, $"Follower '{rel.FollowerName}' is not a channel function of this mode.");
            }
        }

        foreach (var macro in mode.Macros)
        {
            string path = ReadContext.ChildPath(modePath + "/FTMacros", macro.Name);
            if (macro.ChannelFunctionName is not null)
            {
                macro.ChannelFunction = nodes.Find(macro.ChannelFunctionName).Function;
                if (macro.ChannelFunction is null) ctx.Warn(path, $"Channel function '{macro.ChannelFunctionName}' is not defined.");
            }
            foreach (var step in macro.Steps)
                foreach (var v in step.Values)
                {
                    if (v.DmxChannelName is null) continue;
                    v.DmxChannel = nodes.Find(v.DmxChannelName).Channel;
                    if (v.DmxChannel is null) ctx.Warn(path, $"DMX channel '{v.DmxChannelName}' is not defined.");
                }
        }
        mode.InvalidateLookups();
    }

    private static bool IsNoFeature(string name) => name.Trim().Equals("NoFeature", StringComparison.OrdinalIgnoreCase);

    private static void ResolveFunction(ReadContext ctx, ChannelFunction f, string path, DefinitionIndex names)
    {
        var attributes = names.Attributes;
        var profiles = names.Profiles;
        if (f.AttributeName is not null && !IsNoFeature(f.AttributeName))
        {
            f.Attribute = attributes.Find(f.AttributeName);
            if (f.Attribute is null) ctx.Warn(path, $"Function '{f.Name}': attribute '{f.AttributeName}' is not defined.");
        }
        if (f.WheelName is not null)
        {
            f.Wheel = names.Wheels.Find(f.WheelName);
            if (f.Wheel is null) ctx.Warn(path, $"Function '{f.Name}': wheel '{f.WheelName}' is not defined.");
        }
        if (f.EmitterName is not null)
        {
            f.Emitter = names.Emitters.Find(f.EmitterName);
            if (f.Emitter is null) ctx.Warn(path, $"Function '{f.Name}': emitter '{f.EmitterName}' is not defined.");
        }
        if (f.FilterName is not null)
        {
            f.Filter = names.Filters.Find(f.FilterName);
            if (f.Filter is null) ctx.Warn(path, $"Function '{f.Name}': filter '{f.FilterName}' is not defined.");
        }
        if (f.ColorSpaceName is not null)
        {
            f.ColorSpace = names.ColorSpaces.Find(f.ColorSpaceName);
            if (f.ColorSpace is null) ctx.Warn(path, $"Function '{f.Name}': colour space '{f.ColorSpaceName}' is not defined.");
        }
        if (f.GamutName is not null)
        {
            f.Gamut = names.Gamuts.Find(f.GamutName);
            if (f.Gamut is null) ctx.Warn(path, $"Function '{f.Name}': gamut '{f.GamutName}' is not defined.");
        }
        if (f.DmxProfileName is not null)
        {
            f.DmxProfile = profiles.Find(f.DmxProfileName);
            if (f.DmxProfile is null) ctx.Warn(path, $"Function '{f.Name}': DMX profile '{f.DmxProfileName}' is not defined.");
        }

        foreach (var set in f.ChannelSets)
        {
            if (set.WheelSlotIndex <= 0 || f.Wheel is null) continue;
            set.WheelSlot = f.Wheel.GetSlot(set.WheelSlotIndex);
            if (set.WheelSlot is null)
                ctx.Warn(path, $"Channel set '{set.Name}': slot {set.WheelSlotIndex} does not exist on wheel '{f.Wheel.Name}' ({f.Wheel.Slots.Count} slots).");
        }
        foreach (var sub in f.SubChannelSets)
        {
            if (sub.SubPhysicalUnitName is not null)
            {
                sub.SubPhysicalUnit = FindSubPhysicalUnit(attributes, f.Attribute, sub.SubPhysicalUnitName);
                if (sub.SubPhysicalUnit is null) ctx.Warn(path, $"Sub channel set '{sub.Name}': sub physical unit '{sub.SubPhysicalUnitName}' is not defined.");
            }
            if (sub.DmxProfileName is not null)
            {
                sub.DmxProfile = profiles.Find(sub.DmxProfileName);
                if (sub.DmxProfile is null) ctx.Warn(path, $"Sub channel set '{sub.Name}': DMX profile '{sub.DmxProfileName}' is not defined.");
            }
        }
    }

    /// <summary>Sub-physical units have no name; nodes are written <c>Attribute.Type</c> (or just the type).</summary>
    private static SubPhysicalUnit? FindSubPhysicalUnit(NameIndex<GdtfAttribute> attributes, GdtfAttribute? owner, string node)
    {
        int dot = node.LastIndexOf('.');
        var attribute = dot > 0 ? attributes.Find(node[..dot]) ?? owner : owner;
        string type = (dot >= 0 ? node[(dot + 1)..] : node).Trim();
        return attribute?.SubPhysicalUnits.Find(s => s.Type.ToString().Equals(type, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves node references within one mode — a channel (<c>Geometry_Attribute</c>) or a channel function
    /// (<c>Channel.LogicalChannel.Function</c>) — tolerating other spellings (case, surrounding white space, a channel
    /// prefix spelled differently). Every fallback is a dictionary lookup, so resolving all mode masters, relations
    /// and macros of a mode stays linear; a scan of the channels per reference made hostile files with thousands of
    /// dangling references take minutes.
    /// </summary>
    private sealed class ModeNodeIndex
    {
        private readonly DmxMode _mode;
        private readonly Dictionary<string, DmxChannel> _looseChannels = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (DmxChannel Channel, int Order)> _channelNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DmxChannel, FunctionIndex> _functions = new(ReferenceEqualityComparer.Instance);

        public ModeNodeIndex(DmxMode mode)
        {
            _mode = mode;
            for (int i = 0; i < mode.Channels.Count; i++)
            {
                var c = mode.Channels[i];
                _looseChannels.TryAdd(c.Name.Trim(), c);
                _channelNames.TryAdd(c.Name, (c, i));
            }
        }

        /// <summary>
        /// The channel (and function, when the node names one) of <paramref name="node"/>: exact channel or function
        /// name; channel name trimmed and case-insensitive; else the first channel (in mode order) whose name followed
        /// by a dot starts the node, with the function inside it (see <see cref="FindOwnFunction"/>), if any.
        /// </summary>
        public (DmxChannel? Channel, ChannelFunction? Function) Find(string node)
        {
            var ch = _mode.FindChannel(node);
            if (ch is not null) return (ch, null);
            var fn = _mode.FindFunction(node);
            if (fn is not null) return (fn.LogicalChannel?.Channel, fn);

            string trimmed = node.Trim();
            if (_looseChannels.TryGetValue(trimmed, out ch)) return (ch, null);
            DmxChannel? best = null;
            int bestOrder = int.MaxValue;
            for (int dot = trimmed.IndexOf('.'); dot >= 0; dot = trimmed.IndexOf('.', dot + 1))
            {
                if (_channelNames.TryGetValue(trimmed[..dot], out var hit) && hit.Order < bestOrder)
                    (best, bestOrder) = (hit.Channel, hit.Order);
            }
            return best is null ? (null, null) : (best, FindOwnFunction(best, trimmed));
        }

        /// <summary>
        /// A function of <paramref name="ch"/> named by <paramref name="node"/>: exact node name; node name trimmed and
        /// case-insensitive; <c>LogicalChannel.Function</c> at the end of the node (channel prefix spelled
        /// differently); else the function name after the last dot. The first function in file order wins.
        /// </summary>
        public ChannelFunction? FindOwnFunction(DmxChannel ch, string node)
        {
            if (_mode.FindFunction(node) is { } exact && ReferenceEquals(exact.LogicalChannel?.Channel, ch)) return exact;
            if (!_functions.TryGetValue(ch, out var index)) _functions[ch] = index = new FunctionIndex(ch);
            return index.Find(node);
        }
    }

    /// <summary>Lookups of one channel's functions by the spellings <see cref="ModeNodeIndex.FindOwnFunction"/> accepts.</summary>
    private sealed class FunctionIndex
    {
        private readonly Dictionary<string, ChannelFunction> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ChannelFunction> _loose = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (ChannelFunction Function, int Order)> _tails = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ChannelFunction> _names = new(StringComparer.OrdinalIgnoreCase);

        public FunctionIndex(DmxChannel ch)
        {
            int order = 0;
            foreach (var f in ch.Functions)
            {
                string full = f.FullName;
                _exact.TryAdd(full, f);
                _loose.TryAdd(full.Trim(), f);
                _tails.TryAdd($"{f.LogicalChannel?.Name}.{f.Name}", (f, order++));
                _names.TryAdd(f.Name.Trim(), f);
            }
        }

        public ChannelFunction? Find(string node)
        {
            if (_exact.TryGetValue(node, out var f)) return f;
            string trimmed = node.Trim();
            if (_loose.TryGetValue(trimmed, out f)) return f;

            // "…X.LogicalChannel.Function" or "LogicalChannel.Function": every suffix after a dot is a candidate tail.
            ChannelFunction? best = null;
            int bestOrder = int.MaxValue;
            if (_tails.TryGetValue(trimmed, out var hit)) (best, bestOrder) = (hit.Function, hit.Order);
            for (int dot = trimmed.IndexOf('.'); dot >= 0; dot = trimmed.IndexOf('.', dot + 1))
            {
                if (_tails.TryGetValue(trimmed[(dot + 1)..], out hit) && hit.Order < bestOrder)
                    (best, bestOrder) = (hit.Function, hit.Order);
            }
            if (best is not null) return best;

            int last = trimmed.LastIndexOf('.');
            return _names.TryGetValue(last >= 0 ? trimmed[(last + 1)..] : trimmed, out f) ? f : null;
        }
    }

    // ---------------------------------------------------------------- DMX ranges

    /// <summary>
    /// Sets function DMX ranges and channel set ranges (in channel resolution; <see cref="DmxChannel.Resolution"/> and
    /// the mode masters must be resolved). GDTF: the end of a function is the next function's DMXFrom − 1 or the
    /// channel maximum, the end of a channel set the next set's DMXFrom − 1 or the end of its function. "Next" is taken
    /// in DMX order among the functions that can be active at the same time as this one (see
    /// <see cref="CanBeActiveTogether"/>): files list one function per mode-master range, all starting at 0.
    /// </summary>
    internal static void ComputeRanges(DmxChannel ch) => ComputeRanges(ch, null, "");

    /// <summary>
    /// Most candidates skipped per channel while looking for the function that ends another one's range (real channels
    /// skip a few thousand at most). A hostile channel with tens of thousands of mutually exclusive functions would make
    /// the search quadratic; past this budget the remaining functions end at the next larger DMXFrom, as they would
    /// without mode masters, and a warning says so.
    /// </summary>
    internal const int MaxRangeSkips = 1 << 20;

    /// <inheritdoc cref="ComputeRanges(DmxChannel)"/>
    internal static void ComputeRanges(DmxChannel ch, WarningSink? warnings, string path)
    {
        var functions = ch.Functions.ToList();
        foreach (var f in functions) f.DmxFromValue = ch.ToChannelValue(f.DmxFrom);

        var order = Enumerable.Range(0, functions.Count).OrderBy(i => functions[i].DmxFromValue).ThenBy(i => i).ToArray();
        // Functions with the same DMXFrom never end each other's range: start each search at the next larger DMXFrom.
        var nextLarger = NextLarger(order.Length, i => functions[order[i]].DmxFromValue);
        var conditions = new Condition[functions.Count][];
        for (int i = 0; i < functions.Count; i++) conditions[i] = Conditions(functions[i]);
        long skips = 0;
        bool exhausted = false;
        for (int oi = 0; oi < order.Length; oi++)
        {
            var f = functions[order[oi]];
            uint to = ch.MaxValue;
            for (int oj = nextLarger[oi]; oj < order.Length; oj++)
            {
                var g = functions[order[oj]];
                if (!exhausted)
                {
                    bool together = f.ModeMasterChannel is null || g.ModeMasterChannel is null
                        ? CanBeActiveTogether(f, g) // no master, or one that did not resolve (compared by spelling)
                        : !Exclusive(conditions[order[oi]], conditions[order[oj]]);
                    if (!together)
                    {
                        if (++skips <= MaxRangeSkips) continue;
                        exhausted = true;
                        warnings?.Add(path, $"Channel has too many mutually exclusive functions to work out all DMX ranges; " +
                            "the remaining functions end at the next larger DMXFrom.");
                    }
                }
                to = g.DmxFromValue - 1;
                break;
            }
            f.DmxToValue = to;

            if (f.ChannelSets.Count == 0) continue;
            foreach (var s in f.ChannelSets) s.DmxFromValue = ch.ToChannelValue(s.DmxFrom);
            var sets = f.ChannelSets.OrderBy(s => s.DmxFromValue).ToArray();
            var nextSet = NextLarger(sets.Length, i => sets[i].DmxFromValue);
            for (int si = 0; si < sets.Length; si++)
            {
                uint end = nextSet[si] < sets.Length ? sets[nextSet[si]].DmxFromValue - 1 : f.DmxToValue;
                sets[si].DmxToValue = System.Math.Max(end, sets[si].DmxFromValue);
            }
        }
    }

    /// <summary>
    /// For values sorted ascending: per position, the first later position with a larger value (<paramref name="count"/>
    /// when there is none).
    /// </summary>
    private static int[] NextLarger(int count, Func<int, uint> value)
    {
        var next = new int[count];
        for (int i = count - 1; i >= 0; i--)
            next[i] = i + 1 >= count ? count : value(i + 1) > value(i) ? i + 1 : next[i + 1];
        return next;
    }

    /// <summary>
    /// Two functions of one channel compete for DMX ranges unless only one of them can be active at a time, which their
    /// mode-master conditions decide — followed through master functions, whose own conditions must hold as well (a
    /// colour temperature function mastered by a "CTC range" function that is itself mastered by a colour-mode channel
    /// is only active in that colour mode). They are exclusive when, for some channel, both require values from
    /// disjoint ranges, or both require it to run two different functions (a channel has one active function at a
    /// time). Masters are compared as resolved channels, however they are spelled (channel or function node).
    /// </summary>
    internal static bool CanBeActiveTogether(ChannelFunction a, ChannelFunction b)
    {
        if (a.ModeMasterName is null || b.ModeMasterName is null) return true;
        if (a.ModeMasterChannel is null || b.ModeMasterChannel is null)
        {
            // Unresolved master: only the spelling can tell whether both depend on the same thing.
            if (!string.Equals(a.ModeMasterName.Trim(), b.ModeMasterName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return a.ModeFrom.ToResolution(DmxValue.MaxByteCount) <= b.ModeTo.ToRangeEnd(DmxValue.MaxByteCount) &&
                   b.ModeFrom.ToResolution(DmxValue.MaxByteCount) <= a.ModeTo.ToRangeEnd(DmxValue.MaxByteCount);
        }
        return !Exclusive(Conditions(a), Conditions(b));
    }

    /// <summary>True when two condition lists cannot hold at the same time (see <see cref="CanBeActiveTogether"/>).</summary>
    private static bool Exclusive(Condition[] ca, Condition[] cb)
    {
        foreach (var x in ca)
            foreach (var y in cb)
            {
                if (!ReferenceEquals(x.Channel, y.Channel)) continue;
                if (x.From > y.To || y.From > x.To) return true;
                if (x.Function is { } fx && y.Function is { } fy && !ReferenceEquals(fx, fy)) return true;
            }
        return false;
    }

    /// <summary>Longest chain of master functions followed (real files chain at most three).</summary>
    private const int MaxConditions = 8;

    /// <summary>
    /// One mode-master condition: <see cref="Channel"/> must be in <see cref="From"/> … <see cref="To"/> and, when
    /// <see cref="Function"/> is set, run that function.
    /// </summary>
    private readonly record struct Condition(DmxChannel Channel, uint From, uint To, ChannelFunction? Function);

    /// <summary>
    /// The conditions under which <paramref name="f"/> is active: its own, then its master function's, and so on (empty
    /// without a resolved master; cycles and chains longer than <see cref="MaxConditions"/> are cut).
    /// </summary>
    private static Condition[] Conditions(ChannelFunction f)
    {
        if (f.ModeMasterChannel is null) return Array.Empty<Condition>();
        var list = new List<Condition>(2);
        for (var current = f; current?.ModeMasterChannel is { } master && list.Count < MaxConditions; current = current.ModeMasterFunction)
        {
            if (list.Exists(c => ReferenceEquals(c.Channel, master) && ReferenceEquals(c.Function, current.ModeMasterFunction))) break; // cycle
            list.Add(new Condition(master, current.ModeFromValue, current.ModeToValue, current.ModeMasterFunction));
        }
        return list.ToArray();
    }

    private static int VirtualResolution(DmxChannel ch)
    {
        int bytes = 1;
        void Take(DmxValue v) => bytes = System.Math.Max(bytes, v.ByteCount);
        if (ch.Highlight is { } h) Take(h);
        if (ch.LegacyDefault is { } d) Take(d);
        foreach (var f in ch.Functions)
        {
            Take(f.DmxFrom);
            Take(f.Default);
            foreach (var s in f.ChannelSets) Take(s.DmxFrom);
        }
        return System.Math.Clamp(bytes, 1, DmxValue.MaxByteCount);
    }
}
