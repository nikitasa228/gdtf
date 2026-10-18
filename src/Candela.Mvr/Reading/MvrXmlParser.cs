using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Mvr;

/// <summary>
/// Parses <c>GeneralSceneDescription.xml</c> into an <see cref="MvrDocument"/>. Element and attribute names are matched
/// ignoring case and XML namespaces; every problem becomes an issue and the element is read as far as possible.
/// </summary>
internal sealed class MvrXmlParser
{
    private readonly MvrDocument _doc;
    private readonly IssueSink _sink;
    private readonly HashSet<Guid> _objectIds = new();
    private int _generated;

    public MvrXmlParser(MvrDocument doc)
    {
        _doc = doc;
        _sink = new IssueSink(doc.Issues);
    }

    public void Parse(XDocument x)
    {
        var root = x.Root;
        if (root is null || !Is(root, "GeneralSceneDescription"))
            throw new MvrFormatException($"The scene description's root element is <{root?.Name.LocalName}>, not <GeneralSceneDescription>.");

        ReadVersion(root);
        _doc.Provider = Attr(root, "provider");
        _doc.ProviderVersion = Attr(root, "providerVersion");

        foreach (var e in root.Elements())
        {
            if (Is(e, "UserData")) ReadUserData(e);
            else if (Is(e, "Scene")) ReadScene(e);
            else Unknown("GeneralSceneDescription", e);
        }
        _sink.Complete();
    }

    // ------------------------------------------------------------------ document level

    private void ReadVersion(XElement root)
    {
        string? major = Attr(root, "verMajor");
        string? minor = Attr(root, "verMinor");
        bool okMajor = int.TryParse(major, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vMajor);
        bool okMinor = int.TryParse(minor, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vMinor);
        if (!okMajor || !okMinor)
        {
            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.VersionUnsupported,
                $"The file declares no valid version (verMajor='{major}', verMinor='{minor}'); it is read as MVR 1.6.");
            if (okMajor) _doc.VerMajor = vMajor;
            if (okMinor) _doc.VerMinor = vMinor;
            return;
        }
        _doc.VerMajor = vMajor;
        _doc.VerMinor = vMinor;
        if (vMajor != 1)
        {
            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.VersionUnsupported,
                $"MVR {vMajor}.{vMinor} is not a 1.x version; it is read with the 1.6 rules.");
        }
        else if (vMinor > 6)
        {
            _sink.Add(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.VersionUnsupported,
                $"MVR 1.{vMinor} is newer than 1.6; elements this reader does not know are kept but not interpreted.");
        }
        else if (vMinor < 4)
        {
            _sink.Add(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.VersionUnsupported,
                $"MVR 1.{vMinor} is older than 1.4; it is read with the 1.4–1.6 rules.");
        }
    }

    private void ReadUserData(XElement e)
    {
        foreach (var c in e.Elements())
        {
            if (Is(c, "Data"))
            {
                _doc.UserData.Add(new MvrUserData
                {
                    Provider = Attr(c, "provider") ?? "",
                    Version = Attr(c, "ver") ?? "",
                    Element = new XElement(c),
                });
            }
            else
            {
                Unknown("UserData", c);
            }
        }
    }

    private void ReadScene(XElement e)
    {
        foreach (var c in e.Elements())
        {
            if (Is(c, "AUXData")) ReadAux(c);
            else if (Is(c, "Layers")) ReadLayers(c);
            else Unknown("Scene", c);
        }
    }

    private void ReadAux(XElement e)
    {
        var aux = _doc.AuxData;
        var symdefIds = new HashSet<Guid>();
        foreach (var c in e.Elements())
        {
            if (Is(c, "Class"))
            {
                aux.Classes.Add(new MvrClass { Uuid = DefinitionUuid(c, "Class"), Name = Attr(c, "name") ?? "" });
                ReportUnknownChildren("Class", c, Array.Empty<string>());
            }
            else if (Is(c, "Position"))
            {
                aux.Positions.Add(new MvrPositionDefinition { Uuid = DefinitionUuid(c, "Position"), Name = Attr(c, "name") ?? "" });
                ReportUnknownChildren("Position", c, Array.Empty<string>());
            }
            else if (Is(c, "Symdef"))
            {
                var s = new MvrSymdef { Uuid = DefinitionUuid(c, "Symdef"), Name = Attr(c, "name") ?? "" };
                if (!symdefIds.Add(s.Uuid))
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.UuidDuplicate,
                        $"{Where(c)}Symdef '{s.Name}' repeats uuid {s.Uuid}; symbols use the first definition.");
                }
                foreach (var g in c.Elements())
                {
                    if (Is(g, "ChildList")) ReadGeometryList(g, s.Geometries, $"Symdef '{s.Name}'", Guid.Empty);
                    else if (Is(g, "Geometry3D") || Is(g, "Symbol")) ReadGeometryList(new XElement("ChildList", g), s.Geometries, $"Symdef '{s.Name}'", Guid.Empty, g);
                    else Unknown("Symdef", g);
                }
                aux.Symdefs.Add(s);
            }
            else if (Is(c, "MappingDefinition"))
            {
                aux.MappingDefinitions.Add(ReadMappingDefinition(c));
            }
            else
            {
                aux.UnknownElements.Add(new XElement(c));
                Unknown("AUXData", c, kept: true);
            }
        }
    }

    private MvrMappingDefinition ReadMappingDefinition(XElement e)
    {
        var m = new MvrMappingDefinition { Uuid = DefinitionUuid(e, "MappingDefinition"), Name = Attr(e, "name") ?? "" };
        foreach (var c in e.Elements())
        {
            if (Is(c, "SizeX")) m.SizeX = Int(c, Guid.Empty) ?? 0;
            else if (Is(c, "SizeY")) m.SizeY = Int(c, Guid.Empty) ?? 0;
            else if (Is(c, "Source")) m.Source = ReadSource(c);
            else if (Is(c, "ScaleHandeling") || Is(c, "ScaleHandling")) m.ScaleHandling = ScaleHandling(c, Guid.Empty);
            else Unknown("MappingDefinition", c);
        }
        return m;
    }

    private void ReadLayers(XElement e)
    {
        var ids = new HashSet<Guid>();
        foreach (var c in e.Elements())
        {
            if (!Is(c, "Layer"))
            {
                Unknown("Layers", c);
                continue;
            }
            var layer = new MvrLayer { Name = Attr(c, "name") ?? "" };
            layer.Uuid = DefinitionUuid(c, "Layer");
            if (!ids.Add(layer.Uuid))
            {
                var fresh = DeterministicGuid.Create(layer.Uuid, "duplicate-layer", ids.Count);
                _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.UuidDuplicate,
                    $"{Where(c)}Layer '{layer.Name}' repeats uuid {layer.Uuid}; it was given {fresh}.");
                layer.Uuid = fresh;
                ids.Add(fresh);
            }
            foreach (var l in c.Elements())
            {
                if (Is(l, "Matrix"))
                {
                    if (TryMatrix(l, $"Layer '{layer.Name}'", Guid.Empty, out var m))
                    {
                        layer.Matrix = m;
                        layer.HasMatrix = true;
                    }
                }
                else if (Is(l, "ChildList"))
                {
                    ReadChildList(l, layer.Objects, layer.UnknownElements, "Layer");
                }
                else
                {
                    layer.UnknownElements.Add(new XElement(l));
                    Unknown("Layer", l, kept: true);
                }
            }
            _doc.Layers.Add(layer);
        }
    }

    // ------------------------------------------------------------------ objects

    private void ReadChildList(XElement list, List<MvrObject> target, List<XElement> unknownTarget, string context)
    {
        foreach (var e in list.Elements())
        {
            var obj = CreateObject(e.Name.LocalName);
            if (obj is null)
            {
                unknownTarget.Add(new XElement(e));
                Unknown(context + "/ChildList", e, kept: true);
                continue;
            }
            ReadObject(e, obj);
            target.Add(obj);
        }
    }

    private static MvrObject? CreateObject(string name) => name.ToLowerInvariant() switch
    {
        "sceneobject" => new MvrSceneObject(),
        "groupobject" => new MvrGroupObject(),
        "focuspoint" => new MvrFocusPoint(),
        "fixture" => new MvrFixture(),
        "truss" => new MvrTruss(),
        "support" => new MvrSupport(),
        "videoscreen" => new MvrVideoScreen(),
        "projector" => new MvrProjector(),
        _ => null,
    };

    private void ReadObject(XElement e, MvrObject obj)
    {
        obj.Name = Attr(e, "name") ?? "";
        obj.Uuid = ObjectUuid(e, obj);
        string label = $"{obj.Kind} '{obj.Name}'";

        if (obj is MvrDeviceObject device && Attr(e, "multipatch") is { Length: > 0 } multipatch)
        {
            if (TryGuid(multipatch, out var mp)) device.Multipatch = mp;
            else InvalidValue("multipatch", multipatch, obj.Uuid);
        }

        foreach (var c in e.Elements())
        {
            if (Is(c, "Matrix"))
            {
                if (TryMatrix(c, label, obj.Uuid, out var m))
                {
                    obj.Matrix = m;
                    obj.HasMatrix = true;
                }
                continue;
            }
            if (Is(c, "Classing"))
            {
                obj.Classing = GuidValue(c, obj.Uuid);
                continue;
            }

            bool handled = obj switch
            {
                MvrGroupObject g => ReadGroupChild(g, c),
                MvrFocusPoint f => ReadFocusPointChild(f, c, label),
                MvrDeviceObject d => ReadDeviceChild(d, c, label),
                _ => false,
            };
            if (!handled)
            {
                obj.UnknownElements.Add(new XElement(c));
                Unknown(obj.Kind.ToString(), c, kept: true);
            }
        }
    }

    private bool ReadGroupChild(MvrGroupObject g, XElement c)
    {
        if (!Is(c, "ChildList")) return false;
        ReadChildList(c, g.Children, g.UnknownElements, "GroupObject");
        return true;
    }

    private bool ReadFocusPointChild(MvrFocusPoint f, XElement c, string label)
    {
        if (!Is(c, "Geometries")) return false;
        ReadGeometryList(c, f.Geometries, label, f.Uuid);
        return true;
    }

    private bool ReadDeviceChild(MvrDeviceObject d, XElement c, string label)
    {
        switch (c.Name.LocalName.ToLowerInvariant())
        {
            case "geometries":
                ReadGeometryList(c, d.Geometries, label, d.Uuid);
                return true;
            case "gdtfspec":
                d.GdtfSpec = Text(c);
                return true;
            case "gdtfmode":
                d.GdtfMode = Text(c);
                return true;
            case "castshadow":
                d.CastShadow = Bool(c, d.Uuid);
                return true;
            case "position":
                d.Position = GuidValue(c, d.Uuid);
                return true;
            case "function":
                d.Function = Text(c);
                return true;
            case "fixtureid":
                d.FixtureId = Text(c);
                return true;
            case "fixtureidnumeric":
                d.FixtureIdNumeric = Int(c, d.Uuid);
                return true;
            case "unitnumber":
                d.UnitNumber = Int(c, d.Uuid);
                return true;
            case "childposition":
                d.ChildPosition = Text(c);
                return true;
            case "addresses":
                ReadAddresses(c, d, label);
                return true;
            case "protocols":
                ReadProtocols(c, d);
                return true;
            case "alignments":
                ReadAlignments(c, d);
                return true;
            case "customcommands":
                foreach (var cc in c.Elements())
                {
                    if (Is(cc, "CustomCommand")) d.CustomCommands.Add(cc.Value);
                    else Unknown("CustomCommands", cc);
                }
                return true;
            case "overwrites":
                foreach (var o in c.Elements())
                {
                    if (Is(o, "Overwrite")) d.Overwrites.Add(new MvrOverwrite { Universal = Attr(o, "universal") ?? "", Target = Attr(o, "target") ?? "" });
                    else Unknown("Overwrites", o);
                }
                return true;
            case "connections":
                ReadConnections(c, d);
                return true;
            case "customidtype":
                d.CustomIdType = Int(c, d.Uuid);
                return true;
            case "customid":
                d.CustomId = Int(c, d.Uuid);
                return true;
            case "childlist":
                ReadChildList(c, d.Children, d.UnknownElements, d.Kind.ToString());
                return true;
        }

        switch (d)
        {
            case MvrFixture f:
                return ReadFixtureChild(f, c);
            case MvrSupport s when Is(c, "ChainLength"):
                if (Float(c, s.Uuid) is { } mm) s.ChainLength = (float)(mm * MvrMatrix.MetresPerMillimetre);
                return true;
            case MvrVideoScreen v when Is(c, "Sources"):
                foreach (var src in c.Elements())
                {
                    if (Is(src, "Source")) v.Sources.Add(ReadSource(src));
                    else Unknown("Sources", src);
                }
                return true;
            case MvrProjector p when Is(c, "Projections"):
                foreach (var pr in c.Elements())
                {
                    if (!Is(pr, "Projection"))
                    {
                        Unknown("Projections", pr);
                        continue;
                    }
                    var projection = new MvrProjection();
                    foreach (var x in pr.Elements())
                    {
                        if (Is(x, "Source")) projection.Source = ReadSource(x);
                        else if (Is(x, "ScaleHandeling") || Is(x, "ScaleHandling")) projection.ScaleHandling = ScaleHandling(x, p.Uuid);
                        else Unknown("Projection", x);
                    }
                    p.Projections.Add(projection);
                }
                return true;
        }
        return false;
    }

    private bool ReadFixtureChild(MvrFixture f, XElement c)
    {
        switch (c.Name.LocalName.ToLowerInvariant())
        {
            case "focus":
                f.Focus = GuidValue(c, f.Uuid);
                return true;
            case "dmxinvertpan":
                f.DmxInvertPan = Bool(c, f.Uuid);
                return true;
            case "dmxinverttilt":
                f.DmxInvertTilt = Bool(c, f.Uuid);
                return true;
            case "color":
                f.Color = Color(c, f.Uuid);
                return true;
            case "mappings":
                foreach (var m in c.Elements())
                {
                    if (Is(m, "Mapping")) f.Mappings.Add(ReadMapping(m, f.Uuid));
                    else Unknown("Mappings", m);
                }
                return true;
            case "gobo":
                var gobo = new MvrGobo { FileName = c.Value.Trim() };
                if (Attr(c, "rotation") is { Length: > 0 } rot)
                {
                    if (TryFloat(rot, out float r)) gobo.Rotation = r;
                    else InvalidValue("Gobo rotation", rot, f.Uuid);
                }
                f.Gobo = gobo;
                return true;
        }
        return false;
    }

    private MvrMapping ReadMapping(XElement e, Guid owner)
    {
        var m = new MvrMapping();
        if (Attr(e, "linkedDef") is { } def)
        {
            if (TryGuid(def, out var g)) m.LinkedDefinition = g;
            else InvalidValue("Mapping linkedDef", def, owner);
        }
        foreach (var c in e.Elements())
        {
            switch (c.Name.LocalName.ToLowerInvariant())
            {
                case "ux": m.Ux = Int(c, owner); break;
                case "uy": m.Uy = Int(c, owner); break;
                case "ox": m.Ox = Int(c, owner); break;
                case "oy": m.Oy = Int(c, owner); break;
                case "rz": m.Rz = Float(c, owner) is { } rz ? (float)rz : null; break;
                default: Unknown("Mapping", c); break;
            }
        }
        return m;
    }

    private static MvrSource ReadSource(XElement e) => new()
    {
        LinkedGeometry = Attr(e, "linkedGeometry") ?? "",
        Type = Attr(e, "type") ?? "File",
        Value = e.Value.Trim(),
    };

    private void ReadAddresses(XElement e, MvrDeviceObject d, string label)
    {
        foreach (var c in e.Elements())
        {
            if (Is(c, "Address"))
            {
                string? breakText = Attr(c, "break");
                int dmxBreak = 0;
                if (!string.IsNullOrWhiteSpace(breakText) &&
                    (!int.TryParse(breakText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dmxBreak) || dmxBreak < 0))
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Patch, MvrIssueCodes.AddressInvalid,
                        $"{Where(c)}{label}: DMX break '{breakText}' is not a non-negative integer; the address is skipped.", d.Uuid);
                    continue;
                }
                if (c.Value.Trim() == "0")
                {
                    // Some exporters write address 0 for objects that are not patched.
                    _sink.Aggregate(IssueSeverity.Note, IssueCategory.Patch, MvrIssueCodes.AddressInvalid, "unpatched",
                        n => $"{n} DMX address(es) are 0, which means not patched; no address is created for them.", d.Uuid);
                }
                else if (MvrAddress.TryParse(c.Value, dmxBreak, out var address)) d.Addresses.Add(address!);
                else
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Patch, MvrIssueCodes.AddressInvalid,
                        $"{Where(c)}{label}: DMX address '{c.Value.Trim()}' is neither an absolute address nor universe.address; it is skipped.", d.Uuid);
                }
            }
            else if (Is(c, "Network"))
            {
                var n = new MvrNetwork
                {
                    Geometry = Attr(c, "geometry") ?? "",
                    IPv4 = Attr(c, "ipv4"),
                    SubnetMask = Attr(c, "subnetmask"),
                    IPv6 = Attr(c, "ipv6"),
                    Hostname = Attr(c, "hostname"),
                };
                if (Attr(c, "dhcp") is { Length: > 0 } dhcp)
                {
                    if (TryBool(dhcp, out bool on)) n.Dhcp = on;
                    else InvalidValue("Network dhcp", dhcp, d.Uuid);
                }
                d.Networks.Add(n);
            }
            else
            {
                Unknown("Addresses", c);
            }
        }
    }

    private void ReadProtocols(XElement e, MvrDeviceObject d)
    {
        foreach (var c in e.Elements())
        {
            if (!Is(c, "Protocol"))
            {
                Unknown("Protocols", c);
                continue;
            }
            d.Protocols.Add(new MvrProtocol
            {
                Geometry = Attr(c, "geometry"),
                Name = Attr(c, "name"),
                Type = Attr(c, "type"),
                Version = Attr(c, "version"),
                Transmission = Attr(c, "transmission"),
            });
        }
    }

    private void ReadAlignments(XElement e, MvrDeviceObject d)
    {
        foreach (var c in e.Elements())
        {
            if (!Is(c, "Alignment"))
            {
                Unknown("Alignments", c);
                continue;
            }
            var a = new MvrAlignment { Geometry = Attr(c, "geometry") };
            if (Attr(c, "up") is { Length: > 0 } up)
            {
                if (TryVector(up, out var v)) a.Up = v;
                else InvalidValue("Alignment up", up, d.Uuid);
            }
            if (Attr(c, "direction") is { Length: > 0 } dir)
            {
                if (TryVector(dir, out var v)) a.Direction = v;
                else InvalidValue("Alignment direction", dir, d.Uuid);
            }
            d.Alignments.Add(a);
        }
    }

    private void ReadConnections(XElement e, MvrDeviceObject d)
    {
        foreach (var c in e.Elements())
        {
            if (!Is(c, "Connection"))
            {
                Unknown("Connections", c);
                continue;
            }
            string? to = Attr(c, "toObject");
            if (!TryGuid(to, out var target))
            {
                InvalidValue("Connection toObject", to ?? "", d.Uuid);
                continue;
            }
            d.Connections.Add(new MvrConnection { Own = Attr(c, "own") ?? "", Other = Attr(c, "other") ?? "", ToObject = target });
        }
    }

    // ------------------------------------------------------------------ geometry

    private void ReadGeometryList(XElement list, List<MvrGeometry> target, string label, Guid owner, XElement? lineSource = null)
    {
        foreach (var g in list.Elements())
        {
            var line = lineSource ?? g;
            if (Is(g, "Geometry3D"))
            {
                var geo = new MvrGeometry3D { FileName = (Attr(g, "fileName") ?? "").Trim() };
                if (geo.FileName.Length == 0)
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ValueInvalid,
                        $"{Where(line)}{label}: Geometry3D without fileName is ignored.", NonEmpty(owner));
                    continue;
                }
                ReadGeometryChildren(g, geo, label, owner);
                target.Add(geo);
            }
            else if (Is(g, "Symbol"))
            {
                string? symdef = Attr(g, "symdef");
                if (!TryGuid(symdef, out var symdefId))
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.SymdefMissing,
                        $"{Where(line)}{label}: Symbol has no valid symdef reference ('{symdef}') and is ignored.", NonEmpty(owner));
                    continue;
                }
                var sym = new MvrSymbol { SymdefId = symdefId };
                sym.Uuid = TryGuid(Attr(g, "uuid"), out var su) ? su : DeterministicGuid.Create(DeterministicGuid.MvrNamespace, $"symbol:{LineOf(line)}", _generated++);
                ReadGeometryChildren(g, sym, label, owner);
                target.Add(sym);
            }
            else
            {
                Unknown("Geometries", g);
            }
        }
    }

    private void ReadGeometryChildren(XElement e, MvrGeometry geo, string label, Guid owner)
    {
        foreach (var c in e.Elements())
        {
            if (Is(c, "Matrix"))
            {
                if (TryMatrix(c, label, owner, out var m))
                {
                    geo.Matrix = m;
                    geo.HasMatrix = true;
                }
            }
            else
            {
                geo.UnknownElements.Add(new XElement(c));
                Unknown(e.Name.LocalName, c, kept: true);
            }
        }
    }

    // ------------------------------------------------------------------ values

    private Guid ObjectUuid(XElement e, MvrObject obj)
    {
        string? text = Attr(e, "uuid");
        if (TryGuid(text, out var id))
        {
            if (_objectIds.Add(id)) return id;
            var fresh = DeterministicGuid.Create(id, "duplicate", _generated++);
            _objectIds.Add(fresh);
            _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.UuidDuplicate,
                $"{Where(e)}{obj.Kind} '{obj.Name}' repeats uuid {id}; it was given {fresh}.", fresh);
            return fresh;
        }
        var generated = DeterministicGuid.Create(DeterministicGuid.MvrNamespace, $"{e.Name.LocalName}:{LineOf(e)}:{obj.Name}", _generated++);
        _objectIds.Add(generated);
        _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.UuidMissing,
            $"{Where(e)}{obj.Kind} '{obj.Name}' has no valid uuid ('{text}'); it was given {generated}.", generated);
        return generated;
    }

    private Guid DefinitionUuid(XElement e, string what)
    {
        string? text = Attr(e, "uuid");
        if (TryGuid(text, out var id)) return id;
        var generated = DeterministicGuid.Create(DeterministicGuid.MvrNamespace, $"{what}:{LineOf(e)}:{Attr(e, "name")}", _generated++);
        _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.UuidMissing,
            $"{Where(e)}{what} '{Attr(e, "name")}' has no valid uuid ('{text}'); it was given {generated}.");
        return generated;
    }

    private bool TryMatrix(XElement e, string label, Guid owner, out Matrix4x4 m)
    {
        string text = e.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            m = Matrix4x4.Identity;
            return false; // an empty <Matrix/> means identity
        }
        if (MvrMatrix.TryParse(text, out m)) return true;
        _sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.MatrixInvalid,
            $"{Where(e)}{label}: matrix '{Shorten(text)}' cannot be parsed; identity is used.", NonEmpty(owner));
        return false;
    }

    private static string? Text(XElement e)
    {
        string t = e.Value.Trim();
        return t.Length == 0 ? null : t;
    }

    private int? Int(XElement e, Guid owner)
    {
        string t = e.Value.Trim();
        if (t.Length == 0) return null;
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == System.Math.Floor(d) &&
            d >= int.MinValue && d <= int.MaxValue)
        {
            return (int)d;
        }
        InvalidValue(e.Name.LocalName, t, owner);
        return null;
    }

    private double? Float(XElement e, Guid owner)
    {
        string t = e.Value.Trim();
        if (t.Length == 0) return null;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)) return v;
        InvalidValue(e.Name.LocalName, t, owner);
        return null;
    }

    private bool? Bool(XElement e, Guid owner)
    {
        string t = e.Value.Trim();
        if (t.Length == 0) return null;
        if (TryBool(t, out bool b)) return b;
        InvalidValue(e.Name.LocalName, t, owner);
        return null;
    }

    private Guid? GuidValue(XElement e, Guid owner)
    {
        string t = e.Value.Trim();
        if (t.Length == 0) return null;
        if (TryGuid(t, out var g)) return g;
        InvalidValue(e.Name.LocalName, t, owner);
        return null;
    }

    private CieColor? Color(XElement e, Guid owner)
    {
        string t = e.Value.Trim();
        if (t.Length == 0) return null;
        var parts = t.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 2 or 3 &&
            TryFloat(parts[0], out float x) && TryFloat(parts[1], out float y) &&
            x >= 0 && x <= 1 && y > 0 && y <= 1)
        {
            float bigY = 100f;
            if (parts.Length == 3 && (!TryFloat(parts[2], out bigY) || bigY < 0))
            {
                InvalidValue("Color", t, owner);
                return null;
            }
            return new CieColor(x, y, bigY);
        }
        InvalidValue("Color", t, owner);
        return null;
    }

    private MvrScaleHandling? ScaleHandling(XElement e, Guid owner)
    {
        string t = e.Value.Trim();
        if (t.Length == 0) return null;
        if (Enum.TryParse<MvrScaleHandling>(t, ignoreCase: true, out var s) && Enum.IsDefined(s)) return s;
        InvalidValue(e.Name.LocalName, t, owner);
        return null;
    }

    internal static bool TryBool(string? text, out bool value)
    {
        value = false;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "on":
                value = true;
                return true;
            case "false" or "0" or "no" or "off":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parses a uuid in any .NET text form, and also as 16 hex byte pairs separated by spaces
    /// (<c>"4A B1 94 62 A6 E3 4C 3B B2 5A D8 09 9F 78 17 0C"</c>), which older exporters write and the DIN SPEC 15801
    /// example of <c>Focus</c> and <c>Position</c> shows; the bytes are read in text order. The nil uuid is rejected.
    /// </summary>
    internal static bool TryGuid(string? text, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.AsSpan().Trim();
        if (Guid.TryParse(t, out id)) return id != Guid.Empty;
        Span<char> hex = stackalloc char[32];
        int n = 0;
        foreach (char c in t)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (n == hex.Length || !char.IsAsciiHexDigit(c)) return false;
            hex[n++] = c;
        }
        return n == hex.Length && Guid.TryParseExact(hex, "N", out id) && id != Guid.Empty;
    }

    private static bool TryFloat(string text, out float value)
    {
        bool ok = float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
        return ok;
    }

    private static bool TryVector(string text, out Vector3 v)
    {
        v = default;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !TryFloat(parts[0], out float x) || !TryFloat(parts[1], out float y) || !TryFloat(parts[2], out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }

    private void InvalidValue(string element, string value, Guid owner)
    {
        string sample = Shorten(value);
        _sink.Aggregate(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ValueInvalid, element,
            n => n == 1
                ? $"Invalid value '{sample}' in <{element}> ignored; the default is used."
                : $"Invalid values in <{element}> ignored {n} times (first: '{sample}'); defaults are used.",
            owner);
    }

    /// <summary>
    /// Reports an element this reader does not know. <paramref name="kept"/> tells whether the caller stored it in an
    /// <c>UnknownElements</c> list (objects, geometry, layers, AUXData, child lists), so a document round trip writes it
    /// back; elsewhere (inside containers such as <c>Addresses</c>) it is dropped, and the message says so.
    /// </summary>
    private void Unknown(string context, XElement e, bool kept = false)
    {
        string name = e.Name.LocalName;
        string fate = kept ? "kept in the document" : "not kept: a document round trip drops it";
        _sink.Aggregate(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.UnknownElement, context + "/" + name,
            n => n == 1
                ? $"Unknown element <{name}> in <{context}> ignored ({fate})."
                : $"Unknown element <{name}> in <{context}> ignored {n} times ({fate}).");
    }

    private void ReportUnknownChildren(string context, XElement e, string[] known)
    {
        foreach (var c in e.Elements())
            if (!known.Any(k => Is(c, k))) Unknown(context, c);
    }

    private static bool Is(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static string? Attr(XElement e, string name)
    {
        foreach (var a in e.Attributes())
            if (string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) return a.Value;
        return null;
    }

    private static int LineOf(XElement e) => e is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;

    private static string Where(XElement e) => LineOf(e) is > 0 and var line ? $"Line {line}: " : "";

    private static string Shorten(string text)
    {
        string t = text.Trim();
        return t.Length <= 80 ? t : t[..77] + "...";
    }

    private static Guid[] NonEmpty(Guid id) => id == Guid.Empty ? Array.Empty<Guid>() : new[] { id };
}
