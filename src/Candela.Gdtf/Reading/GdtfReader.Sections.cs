using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Candela.Gdtf;

public static partial class GdtfReader
{
    private static readonly Dictionary<string, PhysicalUnit> PhysicalUnits = ReadContext.MapEnum(
        ("Temperatur", PhysicalUnit.Temperature),
        ("AngularAccc", PhysicalUnit.AngularAcceleration),
        ("Wavelength", PhysicalUnit.WaveLength));

    private static readonly Dictionary<string, SubPhysicalUnitType> SubPhysicalUnitTypes = ReadContext.MapEnum<SubPhysicalUnitType>();
    private static readonly Dictionary<string, InterpolationTo> Interpolations = ReadContext.MapEnum<InterpolationTo>();

    private static readonly Dictionary<string, ColorSpaceMode> ColorSpaceModes = ReadContext.MapEnum(
        ("sRGB", ColorSpaceMode.Srgb), ("ANSI", ColorSpaceMode.Ansi));

    private static readonly Dictionary<string, GdtfPrimitiveType> PrimitiveTypes = ReadContext.MapEnum(
        ("Base1.1", GdtfPrimitiveType.Base1_1), ("Scanner1.1", GdtfPrimitiveType.Scanner1_1),
        ("Conventional1.1", GdtfPrimitiveType.Conventional1_1));

    private static readonly Dictionary<string, LampType> LampTypes = ReadContext.MapEnum(("LED", LampType.Led));
    private static readonly Dictionary<string, BeamType> BeamTypes = ReadContext.MapEnum<BeamType>();
    private static readonly Dictionary<string, LaserColorType> LaserColorTypes = ReadContext.MapEnum(("RGB", LaserColorType.Rgb));
    private static readonly Dictionary<string, WiringComponentType> ComponentTypes = ReadContext.MapEnum<WiringComponentType>();
    private static readonly Dictionary<string, FuseRating> FuseRatings = ReadContext.MapEnum<FuseRating>();
    private static readonly Dictionary<string, WiringOrientation> Orientations = ReadContext.MapEnum<WiringOrientation>();
    private static readonly Dictionary<string, StructureType> StructureTypes = ReadContext.MapEnum<StructureType>();
    private static readonly Dictionary<string, CrossSectionType> CrossSectionTypes = ReadContext.MapEnum<CrossSectionType>();
    private static readonly Dictionary<string, SupportType> SupportTypes = ReadContext.MapEnum<SupportType>();

    private static void ParseFixtureType(ReadContext ctx, XElement e)
    {
        var ft = ctx.FixtureType;
        ft.Name = ReadContext.Text(e, X.Name);
        ft.ShortName = ReadContext.Text(e, X.ShortName);
        ft.LongName = ReadContext.Text(e, X.LongName);
        ft.Manufacturer = ReadContext.Text(e, X.Manufacturer);
        ft.Description = ReadContext.Text(e, X.Description);
        if (string.IsNullOrWhiteSpace(ft.Name)) ctx.Warn("FixtureType", "Name is empty.");

        var id = ReadContext.NonEmpty(e, X.FixtureTypeId);
        if (id is not null)
        {
            if (Guid.TryParse(id.Trim(), out var g)) ft.FixtureTypeId = g;
            else ctx.Warn("FixtureType", $"FixtureTypeID=\"{id}\" is not a GUID.");
        }
        var refFt = ReadContext.NonEmpty(e, X.RefFt);
        if (refFt is not null)
        {
            if (Guid.TryParse(refFt.Trim(), out var g)) ft.RefFixtureTypeId = g;
            else ctx.Warn("FixtureType", $"RefFT=\"{refFt}\" is not a GUID; ignored.");
        }
        ft.Thumbnail = ReadContext.NonEmpty(e, X.Thumbnail);
        ft.ThumbnailOffsetX = ctx.Int(e, X.ThumbnailOffsetX, 0, "FixtureType");
        ft.ThumbnailOffsetY = ctx.Int(e, X.ThumbnailOffsetY, 0, "FixtureType");
        ft.CanHaveChildren = ctx.YesNo(e, X.CanHaveChildren, true, "FixtureType");

        foreach (var child in e.Elements())
        {
            string name = child.Name.LocalName;
            switch (name)
            {
                case "AttributeDefinitions": Guard(ctx, name, () => ParseAttributeDefinitions(ctx, child)); break;
                case "Wheels": Guard(ctx, name, () => ParseWheels(ctx, child)); break;
                case "PhysicalDescriptions": Guard(ctx, name, () => ParsePhysicalDescriptions(ctx, child)); break;
                case "Models": Guard(ctx, name, () => ParseModels(ctx, child)); break;
                case "Geometries": Guard(ctx, name, () => ParseGeometries(ctx, child)); break;
                case "DMXModes": Guard(ctx, name, () => ParseDmxModes(ctx, child)); break;
                case "Revisions": Guard(ctx, name, () => ParseRevisions(ctx, child)); break;
                case "FTPresets": Guard(ctx, name, () => ParsePresets(ctx, child)); break;
                case "Protocols": Guard(ctx, name, () => ParseProtocols(ctx, child)); break;
                default: ctx.Warn("FixtureType", $"Unexpected element <{name}> ignored."); break;
            }
        }
    }

    // ---------------------------------------------------------------- attribute definitions

    private static void ParseAttributeDefinitions(ReadContext ctx, XElement e)
    {
        var defs = ctx.FixtureType.AttributeDefinitions;
        foreach (var ag in e.Elements(X.ActivationGroups).Elements(X.ActivationGroup))
            defs.ActivationGroups.Add(new ActivationGroup { Name = ReadContext.Text(ag, X.Name) });

        foreach (var fge in e.Elements(X.FeatureGroups).Elements(X.FeatureGroup))
        {
            var fg = new FeatureGroup { Name = ReadContext.Text(fge, X.Name), Pretty = ReadContext.Text(fge, X.Pretty) };
            foreach (var fe in fge.Elements(X.Feature))
                fg.Features.Add(new Feature { Name = ReadContext.Text(fe, X.Name), Group = fg });
            defs.FeatureGroups.Add(fg);
        }

        foreach (var ae in e.Elements(X.Attributes).Elements(X.Attribute))
        {
            string name = ReadContext.Text(ae, X.Name);
            string path = $"Attributes/{name}";
            var a = new GdtfAttribute
            {
                Name = name,
                Pretty = ReadContext.Text(ae, X.Pretty),
                ActivationGroupName = ReadContext.NonEmpty(ae, X.ActivationGroup),
                FeatureName = ReadContext.NonEmpty(ae, X.Feature),
                MainAttributeName = ReadContext.NonEmpty(ae, X.MainAttribute),
                PhysicalUnit = ctx.Enum(ae, X.PhysicalUnit, PhysicalUnits, PhysicalUnit.None, path),
                Color = ctx.ColorOpt(ae, X.Color, path),
            };
            foreach (var se in ae.Elements(X.SubPhysicalUnit))
            {
                a.SubPhysicalUnits.Add(new SubPhysicalUnit
                {
                    Type = ctx.Enum(se, X.Type, SubPhysicalUnitTypes, SubPhysicalUnitType.Value, path),
                    PhysicalUnit = ctx.Enum(se, X.PhysicalUnit, PhysicalUnits, PhysicalUnit.None, path),
                    PhysicalFrom = ctx.Float(se, X.PhysicalFrom, 0f, path),
                    PhysicalTo = ctx.Float(se, X.PhysicalTo, 1f, path),
                    Attribute = a,
                });
            }
            defs.Attributes.Add(a);
        }
    }

    // ---------------------------------------------------------------- wheels

    private static void ParseWheels(ReadContext ctx, XElement e)
    {
        foreach (var we in e.Elements(X.Wheel))
        {
            var wheel = new Wheel { Name = ReadContext.Text(we, X.Name) };
            string path = $"Wheels/{wheel.Name}";
            int index = 0;
            foreach (var se in we.Elements(X.Slot))
            {
                var slot = new WheelSlot
                {
                    Wheel = wheel,
                    Index = ++index,
                    Name = ReadContext.Text(se, X.Name),
                    Color = ctx.Color(se, X.Color, WheelSlot.DefaultColor, path),
                    FilterName = ReadContext.NonEmpty(se, X.Filter),
                    MediaFileName = ReadContext.NonEmpty(se, X.MediaFileName),
                };
                if (slot.MediaFileName is not null && ctx.Archive is not null)
                {
                    slot.MediaPath = ctx.Archive.FindWheelMedia(slot.MediaFileName);
                    if (slot.MediaPath is null)
                        ctx.Warn($"{path}/{slot.Name}", $"Media file '{slot.MediaFileName}' is not in the archive.");
                }
                foreach (var fe in se.Elements(X.Facet))
                {
                    var facet = new PrismFacet { Color = ctx.Color(fe, X.Color, WheelSlot.DefaultColor, path) };
                    var rot = ReadContext.Str(fe, X.Rotation);
                    if (GdtfMatrix.TryParseRotation(rot, out _, out var raw))
                    {
                        facet.RawRotation = raw;
                        facet.Transform = new Matrix3x2(raw[0], raw[1], raw[3], raw[4], raw[6], raw[7]);
                    }
                    else if (rot is not null)
                    {
                        ctx.Warn($"{path}/{slot.Name}", $"Facet Rotation=\"{ReadContext.Shorten(rot)}\" is not a 3x3 matrix; identity used.");
                    }
                    slot.Facets.Add(facet);
                }
                var anim = se.Element(X.AnimationSystem);
                if (anim is not null)
                {
                    slot.AnimationSystem = new AnimationSystem
                    {
                        P1 = ctx.Vector2(anim, X.P1, path),
                        P2 = ctx.Vector2(anim, X.P2, path),
                        P3 = ctx.Vector2(anim, X.P3, path),
                        Radius = ctx.Float(anim, X.Radius, 0f, path),
                    };
                }
                wheel.Slots.Add(slot);
            }
            ctx.FixtureType.Wheels.Add(wheel);
        }
    }

    // ---------------------------------------------------------------- physical descriptions

    private static void ParsePhysicalDescriptions(ReadContext ctx, XElement e)
    {
        var pd = ctx.FixtureType.PhysicalDescriptions;
        foreach (var child in e.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Emitters":
                    foreach (var ee in child.Elements(X.Emitter))
                    {
                        var em = new Emitter
                        {
                            Name = ReadContext.Text(ee, X.Name),
                            Color = ctx.ColorOpt(ee, X.Color, "Emitters"),
                            DiodePart = ReadContext.NonEmpty(ee, X.DiodePart),
                        };
                        float? dwl = ctx.FloatOpt(ee, X.DominantWaveLength, $"Emitters/{em.Name}");
                        em.DominantWaveLength = dwl is > 0 ? dwl : null;
                        ParseMeasurements(ctx, ee, em.Measurements, $"Emitters/{em.Name}");
                        pd.Emitters.Add(em);
                    }
                    break;
                case "Filters":
                    foreach (var fe in child.Elements(X.Filter))
                    {
                        var f = new Filter { Name = ReadContext.Text(fe, X.Name) };
                        f.Color = ctx.Color(fe, X.Color, WheelSlot.DefaultColor, $"Filters/{f.Name}");
                        ParseMeasurements(ctx, fe, f.Measurements, $"Filters/{f.Name}");
                        pd.Filters.Add(f);
                    }
                    break;
                case "ColorSpace":
                    pd.ColorSpace = ParseColorSpace(ctx, child);
                    break;
                case "AdditionalColorSpaces":
                    foreach (var cs in child.Elements(X.ColorSpace)) pd.AdditionalColorSpaces.Add(ParseColorSpace(ctx, cs));
                    break;
                case "Gamuts":
                    foreach (var ge in child.Elements(X.Gamut))
                    {
                        var gamut = new Gamut { Name = ReadContext.Text(ge, X.Name) };
                        gamut.Points.AddRange(ReadContext.ParseColorList(ReadContext.Str(ge, X.Points)));
                        if (gamut.Points.Count < 3) ctx.Warn($"Gamuts/{gamut.Name}", "Gamut has fewer than three points.");
                        pd.Gamuts.Add(gamut);
                    }
                    break;
                case "DMXProfiles":
                    foreach (var de in child.Elements(X.DmxProfile))
                    {
                        var prof = new DmxProfile { Name = ReadContext.Text(de, X.Name) };
                        string path = $"DMXProfiles/{prof.Name}";
                        foreach (var pe in de.Elements(X.Point))
                        {
                            prof.Points.Add(new DmxProfilePoint(
                                ctx.Float(pe, X.DmxPercentage, 0f, path), ctx.Float(pe, X.Cfc0, 0f, path),
                                ctx.Float(pe, X.Cfc1, 0f, path), ctx.Float(pe, X.Cfc2, 0f, path), ctx.Float(pe, X.Cfc3, 0f, path)));
                        }
                        prof.Points.Sort((a, b) => a.DmxPercentage.CompareTo(b.DmxPercentage));
                        pd.DmxProfiles.Add(prof);
                    }
                    break;
                case "CRIs":
                    foreach (var ge in child.Elements(X.CriGroup))
                    {
                        var group = new CriGroup { ColorTemperature = ctx.Float(ge, X.ColorTemperature, 6000f, "CRIs") };
                        foreach (var ce in ge.Elements(X.Cri))
                            group.Cris.Add(new Cri(ReadContext.Text(ce, X.Ces), ctx.Int(ce, X.ColorRenderingIndex, 100, "CRIs")));
                        pd.CriGroups.Add(group);
                    }
                    break;
                case "Connectors":
                    foreach (var ce in child.Elements(X.Connector))
                    {
                        string name = ReadContext.Text(ce, X.Name);
                        pd.Connectors.Add(new Connector
                        {
                            Name = name,
                            Type = ReadContext.Text(ce, X.Type),
                            DmxBreak = ctx.Int(ce, X.DmxBreak, 0, $"Connectors/{name}"),
                            Gender = ctx.Int(ce, X.Gender, 0, $"Connectors/{name}"),
                            Length = ctx.Float(ce, X.Length, 0f, $"Connectors/{name}"),
                        });
                    }
                    break;
                case "Properties":
                    ParseProperties(ctx, child, pd.Properties);
                    break;
                default:
                    ctx.Warn("PhysicalDescriptions", $"Unexpected element <{child.Name.LocalName}> ignored.");
                    break;
            }
        }
    }

    private static void ParseMeasurements(ReadContext ctx, XElement owner, List<Measurement> target, string path)
    {
        foreach (var me in owner.Elements(X.Measurement))
        {
            var m = new Measurement
            {
                Physical = ctx.Float(me, X.Physical, 0f, path),
                LuminousIntensity = ctx.FloatOpt(me, X.LuminousIntensity, path),
                Transmission = ctx.FloatOpt(me, X.Transmission, path),
                InterpolationTo = ctx.Enum(me, X.InterpolationTo, Interpolations, InterpolationTo.Linear, path),
            };
            foreach (var pe in me.Elements(X.MeasurementPoint))
                m.Points.Add(new MeasurementPoint(ctx.Float(pe, X.WaveLength, 0f, path), ctx.Float(pe, X.Energy, 0f, path)));
            target.Add(m);
        }
    }

    private static ColorSpace ParseColorSpace(ReadContext ctx, XElement e)
    {
        string name = ReadContext.NonEmpty(e, X.Name) ?? ColorSpace.DefaultName;
        string path = $"ColorSpace/{name}";
        var mode = ctx.Enum(e, X.Mode, ColorSpaceModes, ColorSpaceMode.Srgb, path);
        var cs = ColorSpace.Create(mode, name);
        if (mode != ColorSpaceMode.Custom) return cs;

        var r = ctx.ColorOpt(e, X.Red, path);
        var g = ctx.ColorOpt(e, X.Green, path);
        var b = ctx.ColorOpt(e, X.Blue, path);
        var w = ctx.ColorOpt(e, X.WhitePoint, path);
        if (r is null || g is null || b is null || w is null)
        {
            ctx.Warn(path, "Custom colour space without all primaries and white point; sRGB primaries used.");
            return cs;
        }
        cs.SetPrimaries((r.Value.X, r.Value.Y), (g.Value.X, g.Value.Y), (b.Value.X, b.Value.Y), (w.Value.X, w.Value.Y));
        if (float.IsNaN(cs.Red.BigY))
        {
            ctx.Warn(path, "Custom colour space primaries are degenerate; luminances taken from the file.");
            cs.Red = r.Value with { BigY = r.Value.BigY / 100f };
            cs.Green = g.Value with { BigY = g.Value.BigY / 100f };
            cs.Blue = b.Value with { BigY = b.Value.BigY / 100f };
            cs.WhitePoint = w.Value with { BigY = 1f };
        }
        return cs;
    }

    private static void ParseProperties(ReadContext ctx, XElement e, FixtureProperties props)
    {
        foreach (var child in e.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "OperatingTemperature":
                    props.OperatingTemperatureLow = ctx.Float(child, X.Low, 0f, "Properties");
                    props.OperatingTemperatureHigh = ctx.Float(child, X.High, 40f, "Properties");
                    break;
                case "Weight":
                    float? w = ctx.FloatOpt(child, X.Value, "Properties/Weight");
                    props.Weight = w is > 0 ? w : null;
                    break;
                case "LegHeight":
                    props.LegHeight = ctx.Float(child, X.Value, 0f, "Properties/LegHeight");
                    break;
                case "PowerConsumption":
                    props.PowerConsumptions.Add(new PowerConsumption
                    {
                        Value = ctx.Float(child, X.Value, 0f, "Properties/PowerConsumption"),
                        PowerFactor = ctx.Float(child, X.PowerFactor, 1f, "Properties/PowerConsumption"),
                        ConnectorName = ReadContext.NonEmpty(child, X.Connector),
                        VoltageLow = ctx.Float(child, X.VoltageLow, 90f, "Properties/PowerConsumption"),
                        VoltageHigh = ctx.Float(child, X.VoltageHigh, 240f, "Properties/PowerConsumption"),
                        FrequencyLow = ctx.Float(child, X.FrequencyLow, 50f, "Properties/PowerConsumption"),
                        FrequencyHigh = ctx.Float(child, X.FrequencyHigh, 60f, "Properties/PowerConsumption"),
                    });
                    break;
                default:
                    ctx.Warn("Properties", $"Unexpected element <{child.Name.LocalName}> ignored.");
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- models

    private static void ParseModels(ReadContext ctx, XElement e)
    {
        foreach (var me in e.Elements(X.Model))
        {
            string name = ReadContext.Text(me, X.Name);
            string path = $"Models/{name}";
            var m = new GdtfModel
            {
                Name = name,
                Length = ctx.Float(me, X.Length, 0f, path),
                Width = ctx.Float(me, X.Width, 0f, path),
                Height = ctx.Float(me, X.Height, 0f, path),
                PrimitiveType = ctx.Enum(me, X.PrimitiveType, PrimitiveTypes, GdtfPrimitiveType.Undefined, path),
                File = ReadContext.NonEmpty(me, X.File),
                SvgOffsetX = ctx.Float(me, X.SvgOffsetX, 0f, path),
                SvgOffsetY = ctx.Float(me, X.SvgOffsetY, 0f, path),
                SvgSideOffsetX = ctx.Float(me, X.SvgSideOffsetX, 0f, path),
                SvgSideOffsetY = ctx.Float(me, X.SvgSideOffsetY, 0f, path),
                SvgFrontOffsetX = ctx.Float(me, X.SvgFrontOffsetX, 0f, path),
                SvgFrontOffsetY = ctx.Float(me, X.SvgFrontOffsetY, 0f, path),
            };
            if (m.Length < 0 || m.Width < 0 || m.Height < 0)
            {
                ctx.Warn(path, "Negative model dimension; absolute value used.");
                m.Length = MathF.Abs(m.Length);
                m.Width = MathF.Abs(m.Width);
                m.Height = MathF.Abs(m.Height);
            }
            ctx.FixtureType.Models.Add(m);
        }
    }

    // ---------------------------------------------------------------- geometries

    private const int MaxGeometryDepth = 256;

    private static void ParseGeometries(ReadContext ctx, XElement e)
    {
        foreach (var child in e.Elements())
        {
            var g = ParseGeometry(ctx, child, "Geometries", 0);
            if (g is not null) ctx.FixtureType.Geometries.Add(g);
        }
    }

    private static GdtfGeometry? ParseGeometry(ReadContext ctx, XElement e, string parentPath, int depth)
    {
        GdtfGeometry? g = e.Name.LocalName switch
        {
            "Geometry" => new GdtfGeometry(),
            "Axis" => new AxisGeometry(),
            "FilterBeam" => new FilterBeamGeometry(),
            "FilterColor" => new FilterColorGeometry(),
            "FilterGobo" => new FilterGoboGeometry(),
            "FilterShaper" => new FilterShaperGeometry(),
            "Beam" => new BeamGeometry(),
            "MediaServerLayer" => new MediaServerLayerGeometry(),
            "MediaServerCamera" => new MediaServerCameraGeometry(),
            "MediaServerMaster" => new MediaServerMasterGeometry(),
            "Display" => new DisplayGeometry(),
            "GeometryReference" => new GeometryReference(),
            "Laser" => new LaserGeometry(),
            "WiringObject" => new WiringObjectGeometry(),
            "Inventory" => new InventoryGeometry(),
            "Structure" => new StructureGeometry(),
            "Support" => new SupportGeometry(),
            "Magnet" => new MagnetGeometry(),
            _ => null,
        };
        if (g is null)
        {
            ctx.Warn(parentPath, $"Unknown geometry element <{e.Name.LocalName}> ignored.");
            return null;
        }

        g.Name = ReadContext.Text(e, X.Name);
        string path = $"{parentPath}/{g.Name}";
        g.ModelName = ReadContext.NonEmpty(e, X.Model);
        g.Position = ctx.Matrix(e, X.Position, path);
        ParseGeometryAttributes(ctx, e, g, path);

        foreach (var child in e.Elements())
        {
            string n = child.Name.LocalName;
            if (g is GeometryReference gr && n == "Break")
            {
                int brk = ctx.Int(child, X.DmxBreak, 1, path);
                int offset = ctx.DmxAddress(child, X.DmxOffset, 1, path);
                if (offset < 1)
                {
                    ctx.Warn(path, $"Break DMXOffset {offset} is not a DMX address; 1 used.");
                    offset = 1;
                }
                gr.Breaks.Add(new GeometryBreak(brk, offset));
                continue;
            }
            if (g is WiringObjectGeometry wo && n == "PinPatch")
            {
                wo.PinPatches.Add(new PinPatch
                {
                    ToWiringObjectName = ReadContext.NonEmpty(child, X.ToWiringObject),
                    FromPin = ctx.Int(child, X.FromPin, 0, path),
                    ToPin = ctx.Int(child, X.ToPin, 0, path),
                });
                continue;
            }
            if (g is LaserGeometry laser && n == "Protocol")
            {
                laser.Protocols.Add(ReadContext.Text(child, X.Name));
                continue;
            }
            if (g is GeometryReference)
            {
                ctx.Warn(path, $"GeometryReference cannot have child <{n}>; ignored.");
                continue;
            }
            if (depth >= MaxGeometryDepth)
            {
                ctx.Warn(path, "Geometry tree is nested too deeply; children ignored.");
                break;
            }
            var c = ParseGeometry(ctx, child, path, depth + 1);
            if (c is not null) g.AddChild(c);
        }
        return g;
    }

    private static void ParseGeometryAttributes(ReadContext ctx, XElement e, GdtfGeometry g, string path)
    {
        switch (g)
        {
            case BeamGeometry b:
                // Only attributes the file gives with a valid value are assigned (which marks them as specified); the
                // others keep the GDTF defaults of BeamGeometry.
                if (ctx.EnumOpt(e, X.LampType, LampTypes, path) is { } lampType) b.LampType = lampType;
                if (ctx.FloatOpt(e, X.PowerConsumption, path) is { } power) b.PowerConsumption = power;
                if (ctx.FloatOpt(e, X.LuminousFlux, path) is { } flux) b.LuminousFlux = flux;
                if (ctx.FloatOpt(e, X.ColorTemperature, path) is { } cct) b.ColorTemperature = cct;
                if (ctx.FloatOpt(e, X.BeamAngle, path) is { } beamAngle) b.BeamAngle = beamAngle;
                if (ctx.FloatOpt(e, X.FieldAngle, path) is { } fieldAngle) b.FieldAngle = fieldAngle;
                if (ctx.FloatOpt(e, X.ThrowRatio, path) is { } throwRatio) b.ThrowRatio = throwRatio;
                if (ctx.FloatOpt(e, X.RectangleRatio, path) is { } rectangleRatio) b.RectangleRatio = rectangleRatio;
                if (ctx.FloatOpt(e, X.BeamRadius, path) is { } beamRadius) b.BeamRadius = beamRadius;
                if (ctx.EnumOpt(e, X.BeamType, BeamTypes, path) is { } beamType) b.BeamType = beamType;
                if (ctx.FloatOpt(e, X.ColorRenderingIndex, path) is { } cri) b.ColorRenderingIndex = cri;
                b.EmitterSpectrumName = ReadContext.NonEmpty(e, X.EmitterSpectrum);
                break;
            case DisplayGeometry d:
                d.Texture = ReadContext.NonEmpty(e, X.Texture);
                break;
            case GeometryReference r:
                r.GeometryName = ReadContext.NonEmpty(e, X.Geometry);
                break;
            case LaserGeometry l:
                l.ColorType = ctx.Enum(e, X.ColorType, LaserColorTypes, LaserColorType.Rgb, path);
                l.ColorWaveLength = ctx.FloatOpt(e, X.Color, path);
                l.OutputStrength = ctx.Float(e, X.OutputStrength, 0f, path);
                l.EmitterName = ReadContext.NonEmpty(e, X.Emitter);
                l.BeamDiameter = ctx.Float(e, X.BeamDiameter, 0f, path);
                l.BeamDivergenceMin = ctx.Float(e, X.BeamDivergenceMin, 0f, path);
                l.BeamDivergenceMax = ctx.Float(e, X.BeamDivergenceMax, 0f, path);
                l.ScanAnglePan = ctx.Float(e, X.ScanAnglePan, 0f, path);
                l.ScanAngleTilt = ctx.Float(e, X.ScanAngleTilt, 0f, path);
                l.ScanSpeed = ctx.Float(e, X.ScanSpeed, 0f, path);
                break;
            case WiringObjectGeometry w:
                w.ConnectorType = ReadContext.NonEmpty(e, X.ConnectorType);
                w.ComponentType = ctx.Enum(e, X.ComponentType, ComponentTypes, WiringComponentType.Input, path);
                w.SignalType = ReadContext.NonEmpty(e, X.SignalType)?.Trim();
                w.PinCount = ctx.Int(e, X.PinCount, 0, path);
                w.ElectricalPayLoad = ctx.Float(e, X.ElectricalPayLoad, 0f, path);
                w.VoltageRangeMin = ctx.Float(e, X.VoltageRangeMin, 0f, path);
                w.VoltageRangeMax = ctx.Float(e, X.VoltageRangeMax, 0f, path);
                w.FrequencyRangeMin = ctx.Float(e, X.FrequencyRangeMin, 0f, path);
                w.FrequencyRangeMax = ctx.Float(e, X.FrequencyRangeMax, 0f, path);
                w.MaxPayLoad = ctx.Float(e, X.MaxPayLoad, 0f, path);
                w.Voltage = ctx.Float(e, X.Voltage, 0f, path);
                w.SignalLayer = ctx.Int(e, X.SignalLayer, 0, path);
                w.CosPhi = ctx.Float(e, X.CosPhi, 0f, path);
                w.FuseCurrent = ctx.Float(e, X.FuseCurrent, 0f, path);
                w.FuseRating = ctx.Enum(e, X.FuseRating, FuseRatings, FuseRating.B, path);
                w.Orientation = ctx.Enum(e, X.Orientation, Orientations, WiringOrientation.Left, path);
                w.WireGroup = ReadContext.NonEmpty(e, X.WireGroup);
                break;
            case InventoryGeometry i:
                i.Count = ctx.Int(e, X.Count, 0, path);
                break;
            case StructureGeometry s:
                s.LinkedGeometryName = ReadContext.NonEmpty(e, X.LinkedGeometry);
                s.StructureType = ctx.Enum(e, X.StructureType, StructureTypes, StructureType.CenterLineBased, path);
                s.CrossSectionType = ctx.Enum(e, X.CrossSectionType, CrossSectionTypes, CrossSectionType.TrussFramework, path);
                s.CrossSectionHeight = ctx.Float(e, X.CrossSectionHeight, 0f, path);
                s.CrossSectionWallThickness = ctx.Float(e, X.CrossSectionWallThickness, 0f, path);
                s.TrussCrossSection = ReadContext.NonEmpty(e, X.TrussCrossSection);
                break;
            case SupportGeometry s:
                s.SupportType = ctx.Enum(e, X.SupportType, SupportTypes, SupportType.Rope, path);
                s.RopeCrossSection = ReadContext.NonEmpty(e, X.RopeCrossSection);
                s.RopeOffset = ctx.Vector3(e, X.RopeOffset, path);
                s.Capacity = new Vector3(ctx.Float(e, X.CapacityX, 0f, path), ctx.Float(e, X.CapacityY, 0f, path), ctx.Float(e, X.CapacityZ, 0f, path));
                s.CapacityRotational = new Vector3(ctx.Float(e, X.CapacityXx, 0f, path), ctx.Float(e, X.CapacityYy, 0f, path), ctx.Float(e, X.CapacityZz, 0f, path));
                s.Resistance = new Vector3(ctx.Float(e, X.ResistanceX, 0f, path), ctx.Float(e, X.ResistanceY, 0f, path), ctx.Float(e, X.ResistanceZ, 0f, path));
                s.ResistanceRotational = new Vector3(ctx.Float(e, X.ResistanceXx, 0f, path), ctx.Float(e, X.ResistanceYy, 0f, path), ctx.Float(e, X.ResistanceZz, 0f, path));
                break;
        }
    }

    // ---------------------------------------------------------------- revisions, presets, protocols

    private static void ParseRevisions(ReadContext ctx, XElement e)
    {
        foreach (var re in e.Elements(X.Revision))
        {
            // Some exporters write signed garbage (e.g. -2147483616) for an unknown user; that means "unknown" (0).
            var userText = ReadContext.Str(re, X.UserId);
            long userId = 0;
            if (userText is not null && !long.TryParse(userText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
                ctx.Warn("Revisions", $"UserID=\"{userText}\" is not an integer; 0 used.");
            var rev = new Revision
            {
                Text = ReadContext.Text(re, X.Text),
                UserId = userId is > 0 and <= uint.MaxValue ? (uint)userId : 0u,
                ModifiedBy = ReadContext.NonEmpty(re, X.ModifiedBy),
            };
            var date = ReadContext.NonEmpty(re, X.Date);
            if (date is not null)
            {
                if (DateTime.TryParse(date.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    rev.Date = d;
                else
                    ctx.Warn("Revisions", $"Date=\"{date}\" is not a date; ignored.");
            }
            ctx.FixtureType.Revisions.Add(rev);
        }
    }

    private static void ParsePresets(ReadContext ctx, XElement e)
    {
        foreach (var pe in e.Elements())
        {
            var preset = new FtPreset { ElementName = pe.Name.LocalName };
            foreach (var a in pe.Attributes()) preset.Attributes[a.Name.LocalName] = a.Value;
            ctx.FixtureType.Presets.Add(preset);
        }
    }

    private static void ParseProtocols(ReadContext ctx, XElement e)
    {
        var protocols = ctx.FixtureType.Protocols;
        foreach (var pe in e.Elements())
        {
            if (pe.Name == X.FtRdm)
            {
                var rdm = new RdmInfo
                {
                    ManufacturerId = ctx.Hex(pe, X.ManufacturerId, 0, "Protocols/FTRDM"),
                    DeviceModelId = ctx.Hex(pe, X.DeviceModelId, 0, "Protocols/FTRDM"),
                };
                foreach (var sv in pe.Elements(X.SoftwareVersionId))
                {
                    var version = new RdmSoftwareVersion { Value = ctx.Hex(sv, X.Value, 0, "Protocols/FTRDM") };
                    foreach (var p in sv.Elements(X.DmxPersonality))
                    {
                        version.Personalities.Add(new RdmPersonality
                        {
                            Value = ctx.Hex(p, X.Value, 0, "Protocols/FTRDM"),
                            DmxModeName = ReadContext.Text(p, X.DmxMode),
                        });
                    }
                    rdm.SoftwareVersions.Add(version);
                }
                protocols.Rdm.Add(rdm);
                continue;
            }
            var block = new ProtocolBlock { Name = pe.Name.LocalName };
            foreach (var a in pe.Attributes()) block.Attributes[a.Name.LocalName] = a.Value;
            foreach (var entry in pe.Descendants())
            {
                var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var a in entry.Attributes()) attrs[a.Name.LocalName] = a.Value;
                block.Entries.Add((entry.Name.LocalName, attrs));
            }
            protocols.Others.Add(block);
        }
    }
}
