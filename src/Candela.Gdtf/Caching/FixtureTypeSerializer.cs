namespace Candela.Gdtf;

/// <summary>
/// Binary form of a complete <see cref="FixtureType"/> (the payload of <see cref="FixtureTypeDiskCache"/> files).
/// </summary>
/// <remarks>
/// <para>
/// The data has two sections. The <em>structure</em> holds every object with its values, in file order (geometries
/// depth first), with names as written. The <em>links</em> hold every resolved object reference as an index into the
/// object lists built while reading the structure (attributes, wheels, slots, emitters, geometries, channels,
/// functions …), so a loaded fixture type has exactly the links the reader resolved, with no name lookups.
/// <see cref="DmxMode.ChannelInstances"/> and <see cref="DmxMode.BreakFootprints"/> are rebuilt with
/// <see cref="DmxLayout"/>, which is deterministic. Warnings are stored, so a cached fixture type reports what the
/// original read reported.
/// </para>
/// <para>
/// Bump <see cref="FormatVersion"/> whenever the layout changes. Reading validates counts, indices and enum values and
/// throws <see cref="InvalidDataException"/> on damaged data.
/// </para>
/// </remarks>
internal static class FixtureTypeSerializer
{
    /// <summary>Version of the binary layout below.</summary>
    public const int FormatVersion = 1;

    /// <summary>Deepest geometry tree accepted when reading (the reader keeps at most 257 levels).</summary>
    private const int MaxGeometryDepth = 1024;

    // ======================================================================== writing

    /// <summary>Serialises <paramref name="ft"/>; throws <see cref="InvalidOperationException"/> for links outside the model.</summary>
    public static void Write(FixtureType ft, CacheWriter w)
    {
        var ix = new Indexes(ft);
        WriteStructure(ft, w);
        WriteLinks(ft, w, ix);
    }

    private static void WriteStructure(FixtureType ft, CacheWriter w)
    {
        w.String(ft.DataVersion);
        w.String(ft.Name);
        w.String(ft.ShortName);
        w.String(ft.LongName);
        w.String(ft.Manufacturer);
        w.String(ft.Description);
        w.Guid(ft.FixtureTypeId);
        w.Bool(ft.RefFixtureTypeId.HasValue);
        if (ft.RefFixtureTypeId is { } refFt) w.Guid(refFt);
        w.String(ft.Thumbnail);
        w.Int(ft.ThumbnailOffsetX);
        w.Int(ft.ThumbnailOffsetY);
        w.Bool(ft.CanHaveChildren);
        w.String(ft.SourceFileName);

        // attribute definitions
        var ad = ft.AttributeDefinitions;
        w.Count(ad.ActivationGroups.Count);
        foreach (var g in ad.ActivationGroups) w.String(g.Name);
        w.Count(ad.FeatureGroups.Count);
        foreach (var g in ad.FeatureGroups)
        {
            w.String(g.Name);
            w.String(g.Pretty);
            w.Count(g.Features.Count);
            foreach (var f in g.Features) w.String(f.Name);
        }
        w.Count(ad.Attributes.Count);
        foreach (var a in ad.Attributes)
        {
            w.String(a.Name);
            w.String(a.Pretty);
            w.String(a.ActivationGroupName);
            w.String(a.FeatureName);
            w.String(a.MainAttributeName);
            w.Enum(a.PhysicalUnit);
            w.ColorOpt(a.Color);
            w.Count(a.SubPhysicalUnits.Count);
            foreach (var s in a.SubPhysicalUnits)
            {
                w.Enum(s.Type);
                w.Enum(s.PhysicalUnit);
                w.Float(s.PhysicalFrom);
                w.Float(s.PhysicalTo);
            }
        }

        // wheels
        w.Count(ft.Wheels.Count);
        foreach (var wheel in ft.Wheels)
        {
            w.String(wheel.Name);
            w.Count(wheel.Slots.Count);
            foreach (var s in wheel.Slots)
            {
                w.String(s.Name);
                w.Int(s.Index);
                w.Color(s.Color);
                w.String(s.FilterName);
                w.String(s.MediaFileName);
                w.String(s.MediaPath);
                w.Count(s.Facets.Count);
                foreach (var f in s.Facets)
                {
                    w.Color(f.Color);
                    w.Matrix(f.Transform);
                    w.Count(f.RawRotation.Length);
                    foreach (float v in f.RawRotation) w.Float(v);
                }
                w.Bool(s.AnimationSystem is not null);
                if (s.AnimationSystem is { } anim)
                {
                    w.Vector2(anim.P1);
                    w.Vector2(anim.P2);
                    w.Vector2(anim.P3);
                    w.Float(anim.Radius);
                }
            }
        }

        // physical descriptions
        var pd = ft.PhysicalDescriptions;
        w.Count(pd.Emitters.Count);
        foreach (var e in pd.Emitters)
        {
            w.String(e.Name);
            w.ColorOpt(e.Color);
            w.FloatOpt(e.DominantWaveLength);
            w.String(e.DiodePart);
            WriteMeasurements(w, e.Measurements);
        }
        w.Count(pd.Filters.Count);
        foreach (var f in pd.Filters)
        {
            w.String(f.Name);
            w.Color(f.Color);
            WriteMeasurements(w, f.Measurements);
        }
        WriteColorSpace(w, pd.ColorSpace);
        w.Count(pd.AdditionalColorSpaces.Count);
        foreach (var cs in pd.AdditionalColorSpaces) WriteColorSpace(w, cs);
        w.Count(pd.Gamuts.Count);
        foreach (var g in pd.Gamuts)
        {
            w.String(g.Name);
            w.Count(g.Points.Count);
            foreach (var p in g.Points) w.Color(p);
        }
        w.Count(pd.DmxProfiles.Count);
        foreach (var p in pd.DmxProfiles)
        {
            w.String(p.Name);
            w.Count(p.Points.Count);
            foreach (var pt in p.Points)
            {
                w.Float(pt.DmxPercentage);
                w.Float(pt.Cfc0);
                w.Float(pt.Cfc1);
                w.Float(pt.Cfc2);
                w.Float(pt.Cfc3);
            }
        }
        w.Count(pd.CriGroups.Count);
        foreach (var g in pd.CriGroups)
        {
            w.Float(g.ColorTemperature);
            w.Count(g.Cris.Count);
            foreach (var c in g.Cris)
            {
                w.String(c.Ces);
                w.Int(c.ColorRenderingIndex);
            }
        }
        w.Count(pd.Connectors.Count);
        foreach (var c in pd.Connectors)
        {
            w.String(c.Name);
            w.String(c.Type);
            w.Int(c.DmxBreak);
            w.Int(c.Gender);
            w.Float(c.Length);
        }
        var props = pd.Properties;
        w.Float(props.OperatingTemperatureLow);
        w.Float(props.OperatingTemperatureHigh);
        w.FloatOpt(props.Weight);
        w.Float(props.LegHeight);
        w.Count(props.PowerConsumptions.Count);
        foreach (var p in props.PowerConsumptions)
        {
            w.Float(p.Value);
            w.Float(p.PowerFactor);
            w.String(p.ConnectorName);
            w.Float(p.VoltageLow);
            w.Float(p.VoltageHigh);
            w.Float(p.FrequencyLow);
            w.Float(p.FrequencyHigh);
        }

        // models
        w.Count(ft.Models.Count);
        foreach (var m in ft.Models)
        {
            w.String(m.Name);
            w.Float(m.Length);
            w.Float(m.Width);
            w.Float(m.Height);
            w.Enum(m.PrimitiveType);
            w.String(m.File);
            w.Float(m.SvgOffsetX);
            w.Float(m.SvgOffsetY);
            w.Float(m.SvgSideOffsetX);
            w.Float(m.SvgSideOffsetY);
            w.Float(m.SvgFrontOffsetX);
            w.Float(m.SvgFrontOffsetY);
        }

        // geometries (depth first, children counted)
        w.Count(ft.Geometries.Count);
        foreach (var top in ft.Geometries)
            foreach (var g in top.DescendantsAndSelf())
                WriteGeometry(w, g);

        // DMX modes
        w.Count(ft.DmxModes.Count);
        foreach (var mode in ft.DmxModes)
        {
            w.String(mode.Name);
            w.String(mode.Description);
            w.String(mode.GeometryName);
            w.Count(mode.Channels.Count);
            foreach (var ch in mode.Channels)
            {
                w.String(ch.Name);
                w.Int(ch.DmxBreak);
                w.Bool(ch.BreakIsOverwrite);
                w.Count(ch.Offsets.Length);
                foreach (int o in ch.Offsets) w.Int(o);
                w.Int(ch.Resolution);
                w.String(ch.InitialFunctionName);
                w.DmxOpt(ch.Highlight);
                w.String(ch.GeometryName);
                w.DmxOpt(ch.LegacyDefault);
                w.UInt(ch.DefaultValue);
                w.Count(ch.LogicalChannels.Count);
                foreach (var lc in ch.LogicalChannels)
                {
                    w.String(lc.AttributeName);
                    w.Enum(lc.Snap);
                    w.Enum(lc.Master);
                    w.Float(lc.MibFade);
                    w.Float(lc.DmxChangeTimeLimit);
                    w.Count(lc.Functions.Count);
                    foreach (var f in lc.Functions) WriteFunction(w, f);
                }
            }
            w.Count(mode.Relations.Count);
            foreach (var r in mode.Relations)
            {
                w.String(r.Name);
                w.String(r.MasterName);
                w.String(r.FollowerName);
                w.Enum(r.Type);
            }
            w.Count(mode.Macros.Count);
            foreach (var m in mode.Macros)
            {
                w.String(m.Name);
                w.String(m.ChannelFunctionName);
                w.Count(m.Steps.Count);
                foreach (var s in m.Steps)
                {
                    w.Float(s.Duration);
                    w.Count(s.Values.Count);
                    foreach (var v in s.Values)
                    {
                        w.Dmx(v.Value);
                        w.String(v.DmxChannelName);
                    }
                }
            }
        }

        // revisions, presets, protocols, warnings
        w.Count(ft.Revisions.Count);
        foreach (var r in ft.Revisions)
        {
            w.String(r.Text);
            w.Bool(r.Date.HasValue);
            if (r.Date is { } date)
            {
                w.Long(date.Ticks);
                w.Enum(date.Kind);
            }
            w.UInt(r.UserId);
            w.String(r.ModifiedBy);
        }
        w.Count(ft.Presets.Count);
        foreach (var p in ft.Presets)
        {
            w.String(p.ElementName);
            w.Dictionary(p.Attributes);
        }
        w.Count(ft.Protocols.Rdm.Count);
        foreach (var rdm in ft.Protocols.Rdm)
        {
            w.UInt(rdm.ManufacturerId);
            w.UInt(rdm.DeviceModelId);
            w.Count(rdm.SoftwareVersions.Count);
            foreach (var sv in rdm.SoftwareVersions)
            {
                w.UInt(sv.Value);
                w.Count(sv.Personalities.Count);
                foreach (var p in sv.Personalities)
                {
                    w.UInt(p.Value);
                    w.String(p.DmxModeName);
                }
            }
        }
        w.Count(ft.Protocols.Others.Count);
        foreach (var b in ft.Protocols.Others)
        {
            w.String(b.Name);
            w.Dictionary(b.Attributes);
            w.Count(b.Entries.Count);
            foreach (var (element, attributes) in b.Entries)
            {
                w.String(element);
                w.Dictionary(attributes);
            }
        }
        w.Count(ft.Warnings.Count);
        foreach (var warning in ft.Warnings)
        {
            w.String(warning.Path);
            w.String(warning.Message);
        }
    }

    private static void WriteMeasurements(CacheWriter w, List<Measurement> measurements)
    {
        w.Count(measurements.Count);
        foreach (var m in measurements)
        {
            w.Float(m.Physical);
            w.FloatOpt(m.LuminousIntensity);
            w.FloatOpt(m.Transmission);
            w.Enum(m.InterpolationTo);
            w.Count(m.Points.Count);
            foreach (var p in m.Points)
            {
                w.Float(p.WaveLength);
                w.Float(p.Energy);
            }
        }
    }

    private static void WriteColorSpace(CacheWriter w, ColorSpace cs)
    {
        w.String(cs.Name);
        w.Enum(cs.Mode);
        w.Color(cs.Red);
        w.Color(cs.Green);
        w.Color(cs.Blue);
        w.Color(cs.WhitePoint);
    }

    private static void WriteGeometry(CacheWriter w, GdtfGeometry g)
    {
        w.Enum(g.Type);
        w.String(g.Name);
        w.String(g.ModelName);
        w.Matrix(g.Position);
        switch (g)
        {
            case BeamGeometry b:
                w.Enum(b.Specified);
                w.Enum(b.LampType);
                w.Float(b.PowerConsumption);
                w.Float(b.LuminousFlux);
                w.Float(b.ColorTemperature);
                w.Float(b.BeamAngle);
                w.Float(b.FieldAngle);
                w.Float(b.ThrowRatio);
                w.Float(b.RectangleRatio);
                w.Float(b.BeamRadius);
                w.Enum(b.BeamType);
                w.Float(b.ColorRenderingIndex);
                w.String(b.EmitterSpectrumName);
                break;
            case DisplayGeometry d:
                w.String(d.Texture);
                break;
            case GeometryReference r:
                w.String(r.GeometryName);
                w.Count(r.Breaks.Count);
                foreach (var b in r.Breaks)
                {
                    w.Int(b.DmxBreak);
                    w.Int(b.DmxOffset);
                }
                break;
            case LaserGeometry l:
                w.Enum(l.ColorType);
                w.FloatOpt(l.ColorWaveLength);
                w.Float(l.OutputStrength);
                w.String(l.EmitterName);
                w.Float(l.BeamDiameter);
                w.Float(l.BeamDivergenceMin);
                w.Float(l.BeamDivergenceMax);
                w.Float(l.ScanAnglePan);
                w.Float(l.ScanAngleTilt);
                w.Float(l.ScanSpeed);
                w.Count(l.Protocols.Count);
                foreach (var p in l.Protocols) w.String(p);
                break;
            case WiringObjectGeometry wo:
                w.String(wo.ConnectorType);
                w.Enum(wo.ComponentType);
                w.String(wo.SignalType);
                w.Int(wo.PinCount);
                w.Float(wo.ElectricalPayLoad);
                w.Float(wo.VoltageRangeMin);
                w.Float(wo.VoltageRangeMax);
                w.Float(wo.FrequencyRangeMin);
                w.Float(wo.FrequencyRangeMax);
                w.Float(wo.MaxPayLoad);
                w.Float(wo.Voltage);
                w.Int(wo.SignalLayer);
                w.Float(wo.CosPhi);
                w.Float(wo.FuseCurrent);
                w.Enum(wo.FuseRating);
                w.Enum(wo.Orientation);
                w.String(wo.WireGroup);
                w.Count(wo.PinPatches.Count);
                foreach (var p in wo.PinPatches)
                {
                    w.String(p.ToWiringObjectName);
                    w.Int(p.FromPin);
                    w.Int(p.ToPin);
                }
                break;
            case InventoryGeometry inv:
                w.Int(inv.Count);
                break;
            case StructureGeometry st:
                w.String(st.LinkedGeometryName);
                w.Enum(st.StructureType);
                w.Enum(st.CrossSectionType);
                w.Float(st.CrossSectionHeight);
                w.Float(st.CrossSectionWallThickness);
                w.String(st.TrussCrossSection);
                break;
            case SupportGeometry su:
                w.Enum(su.SupportType);
                w.String(su.RopeCrossSection);
                w.Vector3(su.RopeOffset);
                w.Vector3(su.Capacity);
                w.Vector3(su.CapacityRotational);
                w.Vector3(su.Resistance);
                w.Vector3(su.ResistanceRotational);
                break;
        }
        w.Count(g.Children.Count);
    }

    private static void WriteFunction(CacheWriter w, ChannelFunction f)
    {
        w.String(f.Name);
        w.Int(f.Index);
        w.String(f.AttributeName);
        w.String(f.OriginalAttribute);
        w.Dmx(f.DmxFrom);
        w.UInt(f.DmxFromValue);
        w.UInt(f.DmxToValue);
        w.Dmx(f.Default);
        w.Float(f.PhysicalFrom);
        w.Float(f.PhysicalTo);
        w.Float(f.RealFade);
        w.Float(f.RealAcceleration);
        w.String(f.WheelName);
        w.String(f.EmitterName);
        w.String(f.FilterName);
        w.String(f.ColorSpaceName);
        w.String(f.GamutName);
        w.String(f.ModeMasterName);
        w.Dmx(f.ModeFrom);
        w.Dmx(f.ModeTo);
        w.UInt(f.ModeFromValue);
        w.UInt(f.ModeToValue);
        w.String(f.DmxProfileName);
        w.Float(f.Min);
        w.Float(f.Max);
        w.String(f.CustomName);
        w.Count(f.ChannelSets.Count);
        foreach (var s in f.ChannelSets)
        {
            w.String(s.Name);
            w.Dmx(s.DmxFrom);
            w.UInt(s.DmxFromValue);
            w.UInt(s.DmxToValue);
            w.FloatOpt(s.PhysicalFrom);
            w.FloatOpt(s.PhysicalTo);
            w.Int(s.WheelSlotIndex);
        }
        w.Count(f.SubChannelSets.Count);
        foreach (var s in f.SubChannelSets)
        {
            w.String(s.Name);
            w.Float(s.PhysicalFrom);
            w.Float(s.PhysicalTo);
            w.String(s.SubPhysicalUnitName);
            w.String(s.DmxProfileName);
        }
    }

    private static void WriteLinks(FixtureType ft, CacheWriter w, Indexes ix)
    {
        foreach (var a in ft.AttributeDefinitions.Attributes)
        {
            ix.Link(w, ix.ActivationGroups, a.ActivationGroup);
            ix.Link(w, ix.Features, a.Feature);
            ix.Link(w, ix.Attributes, a.MainAttribute);
            foreach (var s in a.SubPhysicalUnits) ix.Link(w, ix.Attributes, s.Attribute);
        }
        foreach (var g in ft.AttributeDefinitions.ActivationGroups)
        {
            w.Count(g.Attributes.Count);
            foreach (var a in g.Attributes) ix.Link(w, ix.Attributes, a);
        }
        foreach (var wheel in ft.Wheels)
            foreach (var s in wheel.Slots)
            {
                ix.Link(w, ix.Wheels, s.Wheel);
                ix.Link(w, ix.Filters, s.Filter);
            }
        foreach (var p in ft.PhysicalDescriptions.Properties.PowerConsumptions)
        {
            ix.Link(w, ix.Geometries, p.Connector);
            ix.Link(w, ix.Connectors, p.LegacyConnector);
        }
        foreach (var g in ix.Geometries.Items)
        {
            ix.Link(w, ix.Models, g.Model);
            switch (g)
            {
                case BeamGeometry b:
                    ix.Link(w, ix.Emitters, b.EmitterSpectrum);
                    break;
                case GeometryReference r:
                    ix.Link(w, ix.Geometries, r.ReferencedGeometry);
                    break;
                case LaserGeometry l:
                    ix.Link(w, ix.Emitters, l.Emitter);
                    break;
                case WiringObjectGeometry wo:
                    foreach (var p in wo.PinPatches) ix.Link(w, ix.Geometries, p.ToWiringObject);
                    break;
                case StructureGeometry st:
                    ix.Link(w, ix.Geometries, st.LinkedGeometry);
                    break;
            }
        }
        foreach (var mode in ft.DmxModes)
        {
            ix.Link(w, ix.Geometries, mode.Geometry);
            foreach (var ch in mode.Channels)
            {
                ix.Link(w, ix.Functions, ch.InitialFunction);
                ix.Link(w, ix.Geometries, ch.Geometry);
                foreach (var lc in ch.LogicalChannels)
                {
                    ix.Link(w, ix.Attributes, lc.Attribute);
                    foreach (var f in lc.Functions)
                    {
                        ix.Link(w, ix.Attributes, f.Attribute);
                        ix.Link(w, ix.Wheels, f.Wheel);
                        ix.Link(w, ix.Emitters, f.Emitter);
                        ix.Link(w, ix.Filters, f.Filter);
                        ix.Link(w, ix.ColorSpaces, f.ColorSpace);
                        ix.Link(w, ix.Gamuts, f.Gamut);
                        ix.Link(w, ix.Channels, f.ModeMasterChannel);
                        ix.Link(w, ix.Functions, f.ModeMasterFunction);
                        ix.Link(w, ix.DmxProfiles, f.DmxProfile);
                        foreach (var s in f.ChannelSets) ix.Link(w, ix.Slots, s.WheelSlot);
                        foreach (var s in f.SubChannelSets)
                        {
                            ix.Link(w, ix.SubPhysicalUnits, s.SubPhysicalUnit);
                            ix.Link(w, ix.DmxProfiles, s.DmxProfile);
                        }
                    }
                }
            }
            foreach (var r in mode.Relations)
            {
                ix.Link(w, ix.Channels, r.Master);
                ix.Link(w, ix.Functions, r.Follower);
            }
            foreach (var m in mode.Macros)
            {
                ix.Link(w, ix.Functions, m.ChannelFunction);
                foreach (var s in m.Steps)
                    foreach (var v in s.Values) ix.Link(w, ix.Channels, v.DmxChannel);
            }
        }
        foreach (var rdm in ft.Protocols.Rdm)
            foreach (var sv in rdm.SoftwareVersions)
                foreach (var p in sv.Personalities) ix.Link(w, ix.Modes, p.DmxMode);
    }

    // ======================================================================== reading

    /// <summary>Reads a fixture type written by <see cref="Write"/>; throws <see cref="InvalidDataException"/> on damaged data.</summary>
    public static FixtureType Read(CacheReader r)
    {
        var ft = new FixtureType();
        var lists = new Lists();
        ReadStructure(ft, r, lists);
        ReadLinks(ft, r, lists);
        if (!r.AtEnd) throw new InvalidDataException("Fixture type cache data has trailing bytes.");
        foreach (var mode in ft.DmxModes) DmxLayout.Build(mode);
        return ft;
    }

    private static void ReadStructure(FixtureType ft, CacheReader r, Lists lists)
    {
        ft.DataVersion = r.String();
        ft.Name = r.String();
        ft.ShortName = r.String();
        ft.LongName = r.String();
        ft.Manufacturer = r.String();
        ft.Description = r.String();
        ft.FixtureTypeId = r.Guid();
        ft.RefFixtureTypeId = r.Bool() ? r.Guid() : null;
        ft.Thumbnail = r.StringOpt();
        ft.ThumbnailOffsetX = r.Int();
        ft.ThumbnailOffsetY = r.Int();
        ft.CanHaveChildren = r.Bool();
        ft.SourceFileName = r.StringOpt();

        var ad = ft.AttributeDefinitions;
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var g = new ActivationGroup { Name = r.String() };
            ad.ActivationGroups.Add(g);
            lists.ActivationGroups.Add(g);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var g = new FeatureGroup { Name = r.String(), Pretty = r.String() };
            for (int j = 0, m = r.Count(); j < m; j++)
            {
                var f = new Feature { Name = r.String(), Group = g };
                g.Features.Add(f);
                lists.Features.Add(f);
            }
            ad.FeatureGroups.Add(g);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var a = new GdtfAttribute
            {
                Name = r.String(),
                Pretty = r.String(),
                ActivationGroupName = r.StringOpt(),
                FeatureName = r.StringOpt(),
                MainAttributeName = r.StringOpt(),
                PhysicalUnit = r.Enum<PhysicalUnit>(),
                Color = r.ColorOpt(),
            };
            for (int j = 0, m = r.Count(); j < m; j++)
            {
                var s = new SubPhysicalUnit
                {
                    Type = r.Enum<SubPhysicalUnitType>(),
                    PhysicalUnit = r.Enum<PhysicalUnit>(),
                    PhysicalFrom = r.Float(),
                    PhysicalTo = r.Float(),
                };
                a.SubPhysicalUnits.Add(s);
                lists.SubPhysicalUnits.Add(s);
            }
            ad.Attributes.Add(a);
            lists.Attributes.Add(a);
        }

        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var wheel = new Wheel { Name = r.String() };
            for (int j = 0, m = r.Count(); j < m; j++)
            {
                var s = new WheelSlot
                {
                    Name = r.String(),
                    Index = r.Int(),
                    Color = r.Color(),
                    FilterName = r.StringOpt(),
                    MediaFileName = r.StringOpt(),
                    MediaPath = r.StringOpt(),
                };
                for (int k = 0, fc = r.Count(); k < fc; k++)
                {
                    var facet = new PrismFacet { Color = r.Color(), Transform = r.Matrix3x2() };
                    var raw = new float[r.Count(4)];
                    for (int q = 0; q < raw.Length; q++) raw[q] = r.Float();
                    facet.RawRotation = raw;
                    s.Facets.Add(facet);
                }
                if (r.Bool())
                    s.AnimationSystem = new AnimationSystem { P1 = r.Vector2(), P2 = r.Vector2(), P3 = r.Vector2(), Radius = r.Float() };
                wheel.Slots.Add(s);
                lists.Slots.Add(s);
            }
            ft.Wheels.Add(wheel);
            lists.Wheels.Add(wheel);
        }

        var pd = ft.PhysicalDescriptions;
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var e = new Emitter { Name = r.String(), Color = r.ColorOpt(), DominantWaveLength = r.FloatOpt(), DiodePart = r.StringOpt() };
            ReadMeasurements(r, e.Measurements);
            pd.Emitters.Add(e);
            lists.Emitters.Add(e);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var f = new Filter { Name = r.String(), Color = r.Color() };
            ReadMeasurements(r, f.Measurements);
            pd.Filters.Add(f);
            lists.Filters.Add(f);
        }
        pd.ColorSpace = ReadColorSpace(r);
        lists.ColorSpaces.Add(pd.ColorSpace);
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var cs = ReadColorSpace(r);
            pd.AdditionalColorSpaces.Add(cs);
            lists.ColorSpaces.Add(cs);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var g = new Gamut { Name = r.String() };
            for (int j = 0, m = r.Count(12); j < m; j++) g.Points.Add(r.Color());
            pd.Gamuts.Add(g);
            lists.Gamuts.Add(g);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var p = new DmxProfile { Name = r.String() };
            for (int j = 0, m = r.Count(20); j < m; j++)
                p.Points.Add(new DmxProfilePoint(r.Float(), r.Float(), r.Float(), r.Float(), r.Float()));
            pd.DmxProfiles.Add(p);
            lists.DmxProfiles.Add(p);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var g = new CriGroup { ColorTemperature = r.Float() };
            for (int j = 0, m = r.Count(2); j < m; j++) g.Cris.Add(new Cri(r.String(), r.Int()));
            pd.CriGroups.Add(g);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var c = new Connector { Name = r.String(), Type = r.String(), DmxBreak = r.Int(), Gender = r.Int(), Length = r.Float() };
            pd.Connectors.Add(c);
            lists.Connectors.Add(c);
        }
        var props = pd.Properties;
        props.OperatingTemperatureLow = r.Float();
        props.OperatingTemperatureHigh = r.Float();
        props.Weight = r.FloatOpt();
        props.LegHeight = r.Float();
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            props.PowerConsumptions.Add(new PowerConsumption
            {
                Value = r.Float(),
                PowerFactor = r.Float(),
                ConnectorName = r.StringOpt(),
                VoltageLow = r.Float(),
                VoltageHigh = r.Float(),
                FrequencyLow = r.Float(),
                FrequencyHigh = r.Float(),
            });
        }

        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var m = new GdtfModel
            {
                Name = r.String(),
                Length = r.Float(),
                Width = r.Float(),
                Height = r.Float(),
                PrimitiveType = r.Enum<GdtfPrimitiveType>(),
                File = r.StringOpt(),
                SvgOffsetX = r.Float(),
                SvgOffsetY = r.Float(),
                SvgSideOffsetX = r.Float(),
                SvgSideOffsetY = r.Float(),
                SvgFrontOffsetX = r.Float(),
                SvgFrontOffsetY = r.Float(),
            };
            ft.Models.Add(m);
            lists.Models.Add(m);
        }

        // geometries: pre-order with child counts, rebuilt with an explicit stack of (node, children still to read)
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var (top, topChildren) = ReadGeometry(r);
            lists.Geometries.Add(top);
            ft.Geometries.Add(top);
            var stack = new Stack<(GdtfGeometry Node, int Left)>();
            if (topChildren > 0) stack.Push((top, topChildren));
            while (stack.Count > 0)
            {
                var (parent, left) = stack.Pop();
                if (left > 1) stack.Push((parent, left - 1));
                var (child, children) = ReadGeometry(r);
                parent.AddChild(child);
                lists.Geometries.Add(child);
                if (children > 0)
                {
                    if (stack.Count >= MaxGeometryDepth) throw new InvalidDataException("Fixture type cache data is damaged (geometry depth).");
                    stack.Push((child, children));
                }
            }
        }

        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var mode = new DmxMode { Name = r.String(), Description = r.String(), GeometryName = r.StringOpt() };
            for (int c = 0, cn = r.Count(); c < cn; c++)
            {
                var ch = new DmxChannel { Mode = mode, Name = r.String(), DmxBreak = r.Int(), BreakIsOverwrite = r.Bool() };
                int offsetCount = r.Count();
                var offsets = offsetCount == 0 ? Array.Empty<int>() : new int[offsetCount];
                for (int k = 0; k < offsets.Length; k++) offsets[k] = r.Int();
                ch.Offsets = offsets;
                ch.Resolution = r.Int();
                if (ch.Resolution is < 1 or > DmxValue.MaxByteCount) throw new InvalidDataException("Fixture type cache data is damaged (resolution).");
                ch.InitialFunctionName = r.StringOpt();
                ch.Highlight = r.DmxOpt();
                ch.GeometryName = r.StringOpt();
                ch.LegacyDefault = r.DmxOpt();
                ch.DefaultValue = r.UInt();
                for (int l = 0, ln = r.Count(); l < ln; l++)
                {
                    var lc = new LogicalChannel
                    {
                        Channel = ch,
                        AttributeName = r.StringOpt(),
                        Snap = r.Enum<SnapMode>(),
                        Master = r.Enum<ChannelMaster>(),
                        MibFade = r.Float(),
                        DmxChangeTimeLimit = r.Float(),
                    };
                    for (int f = 0, fn = r.Count(); f < fn; f++)
                    {
                        var function = ReadFunction(r, lc);
                        lc.Functions.Add(function);
                        lists.Functions.Add(function);
                    }
                    ch.LogicalChannels.Add(lc);
                }
                mode.Channels.Add(ch);
                lists.Channels.Add(ch);
            }
            for (int k = 0, kn = r.Count(); k < kn; k++)
                mode.Relations.Add(new DmxRelation { Name = r.String(), MasterName = r.StringOpt(), FollowerName = r.StringOpt(), Type = r.Enum<RelationType>() });
            for (int k = 0, kn = r.Count(); k < kn; k++)
            {
                var macro = new FtMacro { Name = r.String(), ChannelFunctionName = r.StringOpt() };
                for (int s = 0, sn = r.Count(); s < sn; s++)
                {
                    var step = new MacroDmxStep { Duration = r.Float() };
                    for (int v = 0, vn = r.Count(); v < vn; v++)
                        step.Values.Add(new MacroDmxValue { Value = r.Dmx(), DmxChannelName = r.StringOpt() });
                    macro.Steps.Add(step);
                }
                mode.Macros.Add(macro);
            }
            ft.DmxModes.Add(mode);
            lists.Modes.Add(mode);
        }

        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var rev = new Revision { Text = r.String() };
            if (r.Bool())
            {
                long ticks = r.Long();
                var kind = r.Enum<DateTimeKind>();
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new InvalidDataException("Fixture type cache data is damaged (date).");
                rev.Date = new DateTime(ticks, kind);
            }
            rev.UserId = r.UInt();
            rev.ModifiedBy = r.StringOpt();
            ft.Revisions.Add(rev);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var p = new FtPreset { ElementName = r.String() };
            r.Dictionary(p.Attributes);
            ft.Presets.Add(p);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var rdm = new RdmInfo { ManufacturerId = r.UInt(), DeviceModelId = r.UInt() };
            for (int j = 0, m = r.Count(); j < m; j++)
            {
                var sv = new RdmSoftwareVersion { Value = r.UInt() };
                for (int k = 0, kn = r.Count(); k < kn; k++)
                    sv.Personalities.Add(new RdmPersonality { Value = r.UInt(), DmxModeName = r.String() });
                rdm.SoftwareVersions.Add(sv);
            }
            ft.Protocols.Rdm.Add(rdm);
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var b = new ProtocolBlock { Name = r.String() };
            r.Dictionary(b.Attributes);
            for (int j = 0, m = r.Count(); j < m; j++)
            {
                string element = r.String();
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                r.Dictionary(attributes);
                b.Entries.Add((element, attributes));
            }
            ft.Protocols.Others.Add(b);
        }
        for (int i = 0, n = r.Count(2); i < n; i++) ft.Warnings.Add(new GdtfWarning(r.String(), r.String()));
    }

    private static void ReadMeasurements(CacheReader r, List<Measurement> target)
    {
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            var m = new Measurement
            {
                Physical = r.Float(),
                LuminousIntensity = r.FloatOpt(),
                Transmission = r.FloatOpt(),
                InterpolationTo = r.Enum<InterpolationTo>(),
            };
            for (int j = 0, pn = r.Count(8); j < pn; j++) m.Points.Add(new MeasurementPoint(r.Float(), r.Float()));
            target.Add(m);
        }
    }

    private static ColorSpace ReadColorSpace(CacheReader r) => new()
    {
        Name = r.String(),
        Mode = r.Enum<ColorSpaceMode>(),
        Red = r.Color(),
        Green = r.Color(),
        Blue = r.Color(),
        WhitePoint = r.Color(),
    };

    private static (GdtfGeometry Geometry, int Children) ReadGeometry(CacheReader r)
    {
        var type = r.Enum<GeometryType>();
        GdtfGeometry g = type switch
        {
            GeometryType.Geometry => new GdtfGeometry(),
            GeometryType.Axis => new AxisGeometry(),
            GeometryType.FilterBeam => new FilterBeamGeometry(),
            GeometryType.FilterColor => new FilterColorGeometry(),
            GeometryType.FilterGobo => new FilterGoboGeometry(),
            GeometryType.FilterShaper => new FilterShaperGeometry(),
            GeometryType.Beam => new BeamGeometry(),
            GeometryType.MediaServerLayer => new MediaServerLayerGeometry(),
            GeometryType.MediaServerCamera => new MediaServerCameraGeometry(),
            GeometryType.MediaServerMaster => new MediaServerMasterGeometry(),
            GeometryType.Display => new DisplayGeometry(),
            GeometryType.GeometryReference => new GeometryReference(),
            GeometryType.Laser => new LaserGeometry(),
            GeometryType.WiringObject => new WiringObjectGeometry(),
            GeometryType.Inventory => new InventoryGeometry(),
            GeometryType.Structure => new StructureGeometry(),
            GeometryType.Support => new SupportGeometry(),
            GeometryType.Magnet => new MagnetGeometry(),
            _ => throw new InvalidDataException($"Fixture type cache data is damaged (geometry type {type})."),
        };
        g.Name = r.String();
        g.ModelName = r.StringOpt();
        g.Position = r.Matrix4();
        switch (g)
        {
            case BeamGeometry b:
                var specified = r.Enum<BeamAttributes>();
                b.LampType = r.Enum<LampType>();
                b.PowerConsumption = r.Float();
                b.LuminousFlux = r.Float();
                b.ColorTemperature = r.Float();
                b.BeamAngle = r.Float();
                b.FieldAngle = r.Float();
                b.ThrowRatio = r.Float();
                b.RectangleRatio = r.Float();
                b.BeamRadius = r.Float();
                b.BeamType = r.Enum<BeamType>();
                b.ColorRenderingIndex = r.Float();
                b.EmitterSpectrumName = r.StringOpt();
                b.Specified = specified; // after the setters, which mark every attribute as specified
                break;
            case DisplayGeometry d:
                d.Texture = r.StringOpt();
                break;
            case GeometryReference gr:
                gr.GeometryName = r.StringOpt();
                for (int i = 0, n = r.Count(2); i < n; i++) gr.Breaks.Add(new GeometryBreak(r.Int(), r.Int()));
                break;
            case LaserGeometry l:
                l.ColorType = r.Enum<LaserColorType>();
                l.ColorWaveLength = r.FloatOpt();
                l.OutputStrength = r.Float();
                l.EmitterName = r.StringOpt();
                l.BeamDiameter = r.Float();
                l.BeamDivergenceMin = r.Float();
                l.BeamDivergenceMax = r.Float();
                l.ScanAnglePan = r.Float();
                l.ScanAngleTilt = r.Float();
                l.ScanSpeed = r.Float();
                for (int i = 0, n = r.Count(); i < n; i++) l.Protocols.Add(r.String());
                break;
            case WiringObjectGeometry wo:
                wo.ConnectorType = r.StringOpt();
                wo.ComponentType = r.Enum<WiringComponentType>();
                wo.SignalType = r.StringOpt();
                wo.PinCount = r.Int();
                wo.ElectricalPayLoad = r.Float();
                wo.VoltageRangeMin = r.Float();
                wo.VoltageRangeMax = r.Float();
                wo.FrequencyRangeMin = r.Float();
                wo.FrequencyRangeMax = r.Float();
                wo.MaxPayLoad = r.Float();
                wo.Voltage = r.Float();
                wo.SignalLayer = r.Int();
                wo.CosPhi = r.Float();
                wo.FuseCurrent = r.Float();
                wo.FuseRating = r.Enum<FuseRating>();
                wo.Orientation = r.Enum<WiringOrientation>();
                wo.WireGroup = r.StringOpt();
                for (int i = 0, n = r.Count(3); i < n; i++)
                    wo.PinPatches.Add(new PinPatch { ToWiringObjectName = r.StringOpt(), FromPin = r.Int(), ToPin = r.Int() });
                break;
            case InventoryGeometry inv:
                inv.Count = r.Int();
                break;
            case StructureGeometry st:
                st.LinkedGeometryName = r.StringOpt();
                st.StructureType = r.Enum<StructureType>();
                st.CrossSectionType = r.Enum<CrossSectionType>();
                st.CrossSectionHeight = r.Float();
                st.CrossSectionWallThickness = r.Float();
                st.TrussCrossSection = r.StringOpt();
                break;
            case SupportGeometry su:
                su.SupportType = r.Enum<SupportType>();
                su.RopeCrossSection = r.StringOpt();
                su.RopeOffset = r.Vector3();
                su.Capacity = r.Vector3();
                su.CapacityRotational = r.Vector3();
                su.Resistance = r.Vector3();
                su.ResistanceRotational = r.Vector3();
                break;
        }
        int children = r.Count();
        return (g, children);
    }

    private static ChannelFunction ReadFunction(CacheReader r, LogicalChannel lc)
    {
        var f = new ChannelFunction
        {
            LogicalChannel = lc,
            Name = r.String(),
            Index = r.Int(),
            AttributeName = r.StringOpt(),
            OriginalAttribute = r.StringOpt(),
            DmxFrom = r.Dmx(),
            DmxFromValue = r.UInt(),
            DmxToValue = r.UInt(),
            Default = r.Dmx(),
            PhysicalFrom = r.Float(),
            PhysicalTo = r.Float(),
            RealFade = r.Float(),
            RealAcceleration = r.Float(),
            WheelName = r.StringOpt(),
            EmitterName = r.StringOpt(),
            FilterName = r.StringOpt(),
            ColorSpaceName = r.StringOpt(),
            GamutName = r.StringOpt(),
            ModeMasterName = r.StringOpt(),
            ModeFrom = r.Dmx(),
            ModeTo = r.Dmx(),
            ModeFromValue = r.UInt(),
            ModeToValue = r.UInt(),
            DmxProfileName = r.StringOpt(),
            Min = r.Float(),
            Max = r.Float(),
            CustomName = r.StringOpt(),
        };
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            f.ChannelSets.Add(new ChannelSet
            {
                Function = f,
                Name = r.String(),
                DmxFrom = r.Dmx(),
                DmxFromValue = r.UInt(),
                DmxToValue = r.UInt(),
                PhysicalFrom = r.FloatOpt(),
                PhysicalTo = r.FloatOpt(),
                WheelSlotIndex = r.Int(),
            });
        }
        for (int i = 0, n = r.Count(); i < n; i++)
        {
            f.SubChannelSets.Add(new SubChannelSet
            {
                Name = r.String(),
                PhysicalFrom = r.Float(),
                PhysicalTo = r.Float(),
                SubPhysicalUnitName = r.StringOpt(),
                DmxProfileName = r.StringOpt(),
            });
        }
        return f;
    }

    private static void ReadLinks(FixtureType ft, CacheReader r, Lists l)
    {
        foreach (var a in ft.AttributeDefinitions.Attributes)
        {
            a.ActivationGroup = r.Link(l.ActivationGroups);
            a.Feature = r.Link(l.Features);
            a.MainAttribute = r.Link(l.Attributes);
            foreach (var s in a.SubPhysicalUnits) s.Attribute = r.Link(l.Attributes);
        }
        foreach (var g in ft.AttributeDefinitions.ActivationGroups)
            for (int i = 0, n = r.Count(); i < n; i++)
                g.Attributes.Add(r.Link(l.Attributes) ?? throw new InvalidDataException("Fixture type cache data is damaged (activation group)."));
        foreach (var wheel in ft.Wheels)
            foreach (var s in wheel.Slots)
            {
                s.Wheel = r.Link(l.Wheels);
                s.Filter = r.Link(l.Filters);
            }
        foreach (var p in ft.PhysicalDescriptions.Properties.PowerConsumptions)
        {
            p.Connector = Typed<WiringObjectGeometry>(r.Link(l.Geometries));
            p.LegacyConnector = r.Link(l.Connectors);
        }
        foreach (var g in l.Geometries)
        {
            g.Model = r.Link(l.Models);
            switch (g)
            {
                case BeamGeometry b:
                    b.EmitterSpectrum = r.Link(l.Emitters);
                    break;
                case GeometryReference gr:
                    gr.ReferencedGeometry = r.Link(l.Geometries);
                    break;
                case LaserGeometry laser:
                    laser.Emitter = r.Link(l.Emitters);
                    break;
                case WiringObjectGeometry wo:
                    foreach (var p in wo.PinPatches) p.ToWiringObject = Typed<WiringObjectGeometry>(r.Link(l.Geometries));
                    break;
                case StructureGeometry st:
                    st.LinkedGeometry = r.Link(l.Geometries);
                    break;
            }
        }
        foreach (var mode in ft.DmxModes)
        {
            mode.Geometry = r.Link(l.Geometries);
            foreach (var ch in mode.Channels)
            {
                ch.InitialFunction = r.Link(l.Functions);
                ch.Geometry = r.Link(l.Geometries);
                foreach (var lc in ch.LogicalChannels)
                {
                    lc.Attribute = r.Link(l.Attributes);
                    foreach (var f in lc.Functions)
                    {
                        f.Attribute = r.Link(l.Attributes);
                        f.Wheel = r.Link(l.Wheels);
                        f.Emitter = r.Link(l.Emitters);
                        f.Filter = r.Link(l.Filters);
                        f.ColorSpace = r.Link(l.ColorSpaces);
                        f.Gamut = r.Link(l.Gamuts);
                        f.ModeMasterChannel = r.Link(l.Channels);
                        f.ModeMasterFunction = r.Link(l.Functions);
                        f.DmxProfile = r.Link(l.DmxProfiles);
                        foreach (var s in f.ChannelSets) s.WheelSlot = r.Link(l.Slots);
                        foreach (var s in f.SubChannelSets)
                        {
                            s.SubPhysicalUnit = r.Link(l.SubPhysicalUnits);
                            s.DmxProfile = r.Link(l.DmxProfiles);
                        }
                    }
                }
            }
            foreach (var rel in mode.Relations)
            {
                rel.Master = r.Link(l.Channels);
                rel.Follower = r.Link(l.Functions);
            }
            foreach (var m in mode.Macros)
            {
                m.ChannelFunction = r.Link(l.Functions);
                foreach (var s in m.Steps)
                    foreach (var v in s.Values) v.DmxChannel = r.Link(l.Channels);
            }
        }
        foreach (var rdm in ft.Protocols.Rdm)
            foreach (var sv in rdm.SoftwareVersions)
                foreach (var p in sv.Personalities) p.DmxMode = r.Link(l.Modes);
    }

    private static T? Typed<T>(GdtfGeometry? g) where T : GdtfGeometry =>
        g is null ? null : g as T ?? throw new InvalidDataException($"Fixture type cache data is damaged (link to a {g.Type}, expected {typeof(T).Name}).");

    // ======================================================================== object lists

    /// <summary>Objects in serialisation order with their indices (writing).</summary>
    private sealed class Indexes
    {
        public Indexes(FixtureType ft)
        {
            var ad = ft.AttributeDefinitions;
            ActivationGroups = new(ad.ActivationGroups);
            Features = new(ad.FeatureGroups.SelectMany(g => g.Features));
            Attributes = new(ad.Attributes);
            SubPhysicalUnits = new(ad.Attributes.SelectMany(a => a.SubPhysicalUnits));
            Wheels = new(ft.Wheels);
            Slots = new(ft.Wheels.SelectMany(w => w.Slots));
            var pd = ft.PhysicalDescriptions;
            Emitters = new(pd.Emitters);
            Filters = new(pd.Filters);
            ColorSpaces = new(pd.AdditionalColorSpaces.Prepend(pd.ColorSpace));
            Gamuts = new(pd.Gamuts);
            DmxProfiles = new(pd.DmxProfiles);
            Connectors = new(pd.Connectors);
            Models = new(ft.Models);
            Geometries = new(ft.Geometries.SelectMany(g => g.DescendantsAndSelf()));
            Modes = new(ft.DmxModes);
            Channels = new(ft.DmxModes.SelectMany(m => m.Channels));
            Functions = new(ft.DmxModes.SelectMany(m => m.Channels).SelectMany(c => c.LogicalChannels).SelectMany(lc => lc.Functions));
        }

        public Index<ActivationGroup> ActivationGroups { get; }
        public Index<Feature> Features { get; }
        public Index<GdtfAttribute> Attributes { get; }
        public Index<SubPhysicalUnit> SubPhysicalUnits { get; }
        public Index<Wheel> Wheels { get; }
        public Index<WheelSlot> Slots { get; }
        public Index<Emitter> Emitters { get; }
        public Index<Filter> Filters { get; }
        public Index<ColorSpace> ColorSpaces { get; }
        public Index<Gamut> Gamuts { get; }
        public Index<DmxProfile> DmxProfiles { get; }
        public Index<Connector> Connectors { get; }
        public Index<GdtfModel> Models { get; }
        public Index<GdtfGeometry> Geometries { get; }
        public Index<DmxMode> Modes { get; }
        public Index<DmxChannel> Channels { get; }
        public Index<ChannelFunction> Functions { get; }

        /// <summary>Writes a link as index + 1 (0 = null).</summary>
        public void Link<T>(CacheWriter w, Index<T> index, T? target) where T : class
        {
            if (target is null)
            {
                w.UInt(0);
                return;
            }
            if (!index.Map.TryGetValue(target, out int i))
                throw new InvalidOperationException($"{typeof(T).Name} '{target}' is linked but not part of the fixture type.");
            w.UInt((uint)i + 1);
        }
    }

    /// <summary>Items of one kind with their positions (first position wins for an item listed twice).</summary>
    private sealed class Index<T> where T : class
    {
        public Index(IEnumerable<T> items)
        {
            Items = items.ToList();
            Map = new Dictionary<T, int>(Items.Count, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < Items.Count; i++) Map.TryAdd(Items[i], i);
        }

        public List<T> Items { get; }

        public Dictionary<T, int> Map { get; }
    }

    /// <summary>Objects in serialisation order (reading).</summary>
    private sealed class Lists
    {
        public List<ActivationGroup> ActivationGroups { get; } = new();
        public List<Feature> Features { get; } = new();
        public List<GdtfAttribute> Attributes { get; } = new();
        public List<SubPhysicalUnit> SubPhysicalUnits { get; } = new();
        public List<Wheel> Wheels { get; } = new();
        public List<WheelSlot> Slots { get; } = new();
        public List<Emitter> Emitters { get; } = new();
        public List<Filter> Filters { get; } = new();
        public List<ColorSpace> ColorSpaces { get; } = new();
        public List<Gamut> Gamuts { get; } = new();
        public List<DmxProfile> DmxProfiles { get; } = new();
        public List<Connector> Connectors { get; } = new();
        public List<GdtfModel> Models { get; } = new();
        public List<GdtfGeometry> Geometries { get; } = new();
        public List<DmxMode> Modes { get; } = new();
        public List<DmxChannel> Channels { get; } = new();
        public List<ChannelFunction> Functions { get; } = new();
    }
}
