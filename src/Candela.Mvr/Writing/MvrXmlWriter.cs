using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Candela.Mvr;

/// <summary>
/// Serialises an <see cref="MvrDocument"/> to <c>GeneralSceneDescription.xml</c> and packs the archive. Output is
/// deterministic: elements follow the MVR 1.6 element order of each node, numbers use invariant round-trip text, zip
/// entries have a fixed time stamp and keep the document's file order.
/// </summary>
internal static class MvrXmlWriter
{
    /// <summary>Time stamp of every zip entry (fixed so equal documents give byte-identical archives).</summary>
    public static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        CloseOutput = false,
    };

    public static void WritePackage(MvrDocument doc, Stream output)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
        var scene = zip.CreateEntry(MvrReader.SceneDescriptionEntry, CompressionLevel.Optimal);
        scene.LastWriteTime = EntryTime;
        using (var s = scene.Open()) WriteXml(doc, s);

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MvrReader.SceneDescriptionEntry };
        foreach (var file in doc.Files)
        {
            if (file.Bytes is null || !written.Add(file.Name)) continue;
            var level = file.Kind is MvrFileKind.Gdtf or MvrFileKind.Image ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(file.Name, level);
            entry.LastWriteTime = EntryTime;
            using var es = entry.Open();
            es.Write(file.Bytes);
        }
    }

    public static void WriteXml(MvrDocument doc, Stream output)
    {
        using var w = XmlWriter.Create(output, Settings);
        w.WriteStartDocument();
        w.WriteStartElement("GeneralSceneDescription");
        Attr(w, "verMajor", I(doc.VerMajor));
        Attr(w, "verMinor", I(doc.VerMinor));
        if (!string.IsNullOrEmpty(doc.Provider)) Attr(w, "provider", doc.Provider);
        if (!string.IsNullOrEmpty(doc.ProviderVersion)) Attr(w, "providerVersion", doc.ProviderVersion);

        if (doc.UserData.Count > 0)
        {
            w.WriteStartElement("UserData");
            foreach (var u in doc.UserData)
            {
                if (u.Element is not null)
                {
                    var e = new XElement(u.Element);
                    SetAttribute(e, "provider", u.Provider);
                    SetAttribute(e, "ver", u.Version);
                    e.WriteTo(w);
                }
                else
                {
                    w.WriteStartElement("Data");
                    Attr(w, "provider", u.Provider);
                    Attr(w, "ver", u.Version);
                    w.WriteEndElement();
                }
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("Scene");
        WriteAux(w, doc.AuxData);
        w.WriteStartElement("Layers");
        foreach (var layer in doc.Layers)
        {
            w.WriteStartElement("Layer");
            Attr(w, "uuid", Id(layer.Uuid));
            Attr(w, "name", layer.Name);
            if (layer.HasMatrix || !MvrMatrix.IsIdentity(layer.Matrix)) Elem(w, "Matrix", MvrMatrix.Format(layer.Matrix));
            Unknown(w, layer.UnknownElements);
            WriteChildList(w, layer.Objects, alwaysWrite: true);
            w.WriteEndElement();
        }
        w.WriteEndElement(); // Layers
        w.WriteEndElement(); // Scene
        w.WriteEndElement(); // GeneralSceneDescription
        w.WriteEndDocument();
    }

    private static void WriteAux(XmlWriter w, MvrAuxData aux)
    {
        w.WriteStartElement("AUXData");
        foreach (var c in aux.Classes)
        {
            w.WriteStartElement("Class");
            Attr(w, "uuid", Id(c.Uuid));
            Attr(w, "name", c.Name);
            w.WriteEndElement();
        }
        foreach (var s in aux.Symdefs)
        {
            w.WriteStartElement("Symdef");
            Attr(w, "uuid", Id(s.Uuid));
            Attr(w, "name", s.Name);
            w.WriteStartElement("ChildList");
            foreach (var g in s.Geometries) WriteGeometry(w, g);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        foreach (var p in aux.Positions)
        {
            w.WriteStartElement("Position");
            Attr(w, "uuid", Id(p.Uuid));
            Attr(w, "name", p.Name);
            w.WriteEndElement();
        }
        foreach (var m in aux.MappingDefinitions)
        {
            w.WriteStartElement("MappingDefinition");
            Attr(w, "uuid", Id(m.Uuid));
            Attr(w, "name", m.Name);
            Elem(w, "SizeX", I(m.SizeX));
            Elem(w, "SizeY", I(m.SizeY));
            if (m.Source is not null) WriteSource(w, m.Source);
            if (m.ScaleHandling is { } sh) Elem(w, "ScaleHandeling", sh.ToString());
            w.WriteEndElement();
        }
        Unknown(w, aux.UnknownElements);
        w.WriteEndElement();
    }

    private static void WriteChildList(XmlWriter w, IReadOnlyList<MvrObject> objects, bool alwaysWrite)
    {
        if (objects.Count == 0 && !alwaysWrite) return;
        w.WriteStartElement("ChildList");
        foreach (var o in objects) WriteObject(w, o);
        w.WriteEndElement();
    }

    private static void WriteObject(XmlWriter w, MvrObject o)
    {
        w.WriteStartElement(o.Kind.ToString());
        Attr(w, "uuid", Id(o.Uuid));
        Attr(w, "name", o.Name);
        if (o is MvrDeviceObject { Multipatch: { } mp }) Attr(w, "multipatch", Id(mp));

        if (o.HasMatrix || !MvrMatrix.IsIdentity(o.Matrix)) Elem(w, "Matrix", MvrMatrix.Format(o.Matrix));
        if (o.Classing is { } cls) Elem(w, "Classing", Id(cls));

        switch (o)
        {
            case MvrGroupObject g:
                Unknown(w, g.UnknownElements);
                WriteChildList(w, g.Children, alwaysWrite: true);
                break;
            case MvrFocusPoint f:
                WriteGeometries(w, f.Geometries, alwaysWrite: true);
                Unknown(w, f.UnknownElements);
                break;
            case MvrFixture fx:
                WriteFixture(w, fx);
                break;
            case MvrDeviceObject d:
                WriteDevice(w, d);
                break;
        }
        w.WriteEndElement();
    }

    private static void WriteFixture(XmlWriter w, MvrFixture f)
    {
        Opt(w, "GDTFSpec", f.GdtfSpec);
        Opt(w, "GDTFMode", f.GdtfMode);
        if (f.Focus is { } focus) Elem(w, "Focus", Id(focus));
        Opt(w, "CastShadow", f.CastShadow);
        Opt(w, "DMXInvertPan", f.DmxInvertPan);
        Opt(w, "DMXInvertTilt", f.DmxInvertTilt);
        if (f.Position is { } pos) Elem(w, "Position", Id(pos));
        Opt(w, "Function", f.Function);
        Opt(w, "FixtureID", f.FixtureId);
        Opt(w, "FixtureIDNumeric", f.FixtureIdNumeric);
        Opt(w, "UnitNumber", f.UnitNumber);
        Opt(w, "ChildPosition", f.ChildPosition);
        WriteAddresses(w, f, alwaysWrite: true);
        WriteProtocols(w, f);
        WriteAlignments(w, f);
        WriteCustomCommands(w, f);
        WriteOverwrites(w, f);
        WriteConnections(w, f);
        if (f.Color is { } c) Elem(w, "Color", $"{F(c.X)},{F(c.Y)},{F(c.BigY)}");
        Opt(w, "CustomIdType", f.CustomIdType);
        Opt(w, "CustomId", f.CustomId);
        if (f.Mappings.Count > 0)
        {
            w.WriteStartElement("Mappings");
            foreach (var m in f.Mappings)
            {
                w.WriteStartElement("Mapping");
                Attr(w, "linkedDef", Id(m.LinkedDefinition));
                Opt(w, "ux", m.Ux);
                Opt(w, "uy", m.Uy);
                Opt(w, "ox", m.Ox);
                Opt(w, "oy", m.Oy);
                if (m.Rz is { } rz) Elem(w, "rz", F(rz));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        if (f.Gobo is { } gobo)
        {
            w.WriteStartElement("Gobo");
            Attr(w, "rotation", F(gobo.Rotation));
            Text(w, gobo.FileName);
            w.WriteEndElement();
        }
        WriteGeometries(w, f.Geometries, alwaysWrite: false);
        Unknown(w, f.UnknownElements);
        WriteChildList(w, f.Children, alwaysWrite: false);
    }

    private static void WriteDevice(XmlWriter w, MvrDeviceObject d)
    {
        bool positionFirst = d is MvrTruss or MvrSupport;
        if (positionFirst && d.Position is { } p0) Elem(w, "Position", Id(p0));
        WriteGeometries(w, d.Geometries, alwaysWrite: true);
        switch (d)
        {
            case MvrVideoScreen v when v.Sources.Count > 0:
                w.WriteStartElement("Sources");
                foreach (var s in v.Sources) WriteSource(w, s);
                w.WriteEndElement();
                break;
            case MvrProjector pr: // Projections is mandatory on a Projector, even when empty
                w.WriteStartElement("Projections");
                foreach (var p in pr.Projections)
                {
                    w.WriteStartElement("Projection");
                    if (p.Source is not null) WriteSource(w, p.Source);
                    if (p.ScaleHandling is { } sh) Elem(w, "ScaleHandeling", sh.ToString());
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                break;
        }
        Opt(w, "Function", d.Function);
        if (d is MvrSupport { ChainLength: { } chain }) Elem(w, "ChainLength", MvrMatrix.Millimetres(chain));
        Opt(w, "GDTFSpec", d.GdtfSpec);
        Opt(w, "GDTFMode", d.GdtfMode);
        Opt(w, "CastShadow", d.CastShadow);
        if (!positionFirst && d.Position is { } p1) Elem(w, "Position", Id(p1));
        WriteAddresses(w, d, alwaysWrite: false);
        WriteProtocols(w, d);
        WriteAlignments(w, d);
        WriteCustomCommands(w, d);
        WriteOverwrites(w, d);
        WriteConnections(w, d);
        Opt(w, "ChildPosition", d.ChildPosition);
        Opt(w, "FixtureID", d.FixtureId);
        Opt(w, "FixtureIDNumeric", d.FixtureIdNumeric);
        Opt(w, "UnitNumber", d.UnitNumber);
        Opt(w, "CustomId", d.CustomId);
        Opt(w, "CustomIdType", d.CustomIdType);
        Unknown(w, d.UnknownElements);
        WriteChildList(w, d.Children, alwaysWrite: false);
    }

    private static void WriteGeometries(XmlWriter w, List<MvrGeometry> geometries, bool alwaysWrite)
    {
        if (geometries.Count == 0 && !alwaysWrite) return;
        w.WriteStartElement("Geometries");
        foreach (var g in geometries) WriteGeometry(w, g);
        w.WriteEndElement();
    }

    private static void WriteGeometry(XmlWriter w, MvrGeometry g)
    {
        switch (g)
        {
            case MvrGeometry3D g3:
                w.WriteStartElement("Geometry3D");
                Attr(w, "fileName", g3.FileName);
                break;
            case MvrSymbol s:
                w.WriteStartElement("Symbol");
                Attr(w, "uuid", Id(s.Uuid));
                Attr(w, "symdef", Id(s.SymdefId));
                break;
            default:
                return;
        }
        if (g.HasMatrix || !MvrMatrix.IsIdentity(g.Matrix)) Elem(w, "Matrix", MvrMatrix.Format(g.Matrix));
        Unknown(w, g.UnknownElements);
        w.WriteEndElement();
    }

    private static void WriteAddresses(XmlWriter w, MvrDeviceObject d, bool alwaysWrite)
    {
        if (d.Addresses.Count == 0 && d.Networks.Count == 0 && !alwaysWrite) return;
        w.WriteStartElement("Addresses");
        foreach (var a in d.Addresses)
        {
            w.WriteStartElement("Address");
            Attr(w, "break", I(a.Break));
            bool rawOk = a.RawValue is not null && MvrAddress.TryParse(a.RawValue, a.Break, out var parsed) && parsed!.Absolute == a.Absolute;
            Text(w, rawOk ? a.RawValue!.Trim() : I(a.Absolute));
            w.WriteEndElement();
        }
        foreach (var n in d.Networks)
        {
            w.WriteStartElement("Network");
            Attr(w, "geometry", n.Geometry);
            OptAttr(w, "ipv4", n.IPv4);
            OptAttr(w, "subnetmask", n.SubnetMask);
            OptAttr(w, "ipv6", n.IPv6);
            if (n.Dhcp is { } dhcp) Attr(w, "dhcp", dhcp ? "on" : "off");
            OptAttr(w, "hostname", n.Hostname);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteProtocols(XmlWriter w, MvrDeviceObject d)
    {
        if (d.Protocols.Count == 0) return;
        w.WriteStartElement("Protocols");
        foreach (var p in d.Protocols)
        {
            w.WriteStartElement("Protocol");
            OptAttr(w, "geometry", p.Geometry);
            OptAttr(w, "name", p.Name);
            OptAttr(w, "type", p.Type);
            OptAttr(w, "version", p.Version);
            OptAttr(w, "transmission", p.Transmission);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteAlignments(XmlWriter w, MvrDeviceObject d)
    {
        if (d.Alignments.Count == 0) return;
        w.WriteStartElement("Alignments");
        foreach (var a in d.Alignments)
        {
            w.WriteStartElement("Alignment");
            OptAttr(w, "geometry", a.Geometry);
            Attr(w, "up", V(a.Up));
            Attr(w, "direction", V(a.Direction));
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteCustomCommands(XmlWriter w, MvrDeviceObject d)
    {
        if (d.CustomCommands.Count == 0) return;
        w.WriteStartElement("CustomCommands");
        foreach (var c in d.CustomCommands) Elem(w, "CustomCommand", c);
        w.WriteEndElement();
    }

    private static void WriteOverwrites(XmlWriter w, MvrDeviceObject d)
    {
        if (d.Overwrites.Count == 0) return;
        w.WriteStartElement("Overwrites");
        foreach (var o in d.Overwrites)
        {
            w.WriteStartElement("Overwrite");
            Attr(w, "universal", o.Universal);
            if (o.Target.Length > 0) Attr(w, "target", o.Target);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteConnections(XmlWriter w, MvrDeviceObject d)
    {
        if (d.Connections.Count == 0) return;
        w.WriteStartElement("Connections");
        foreach (var c in d.Connections)
        {
            w.WriteStartElement("Connection");
            Attr(w, "own", c.Own);
            Attr(w, "other", c.Other);
            Attr(w, "toObject", Id(c.ToObject));
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteSource(XmlWriter w, MvrSource s)
    {
        w.WriteStartElement("Source");
        Attr(w, "linkedGeometry", s.LinkedGeometry);
        Attr(w, "type", s.Type);
        Text(w, s.Value);
        w.WriteEndElement();
    }

    /// <summary>
    /// Sets an attribute of a kept element (matching its name ignoring case, as the reader does). An empty value does
    /// not add an attribute the element did not have, so a missing <c>ver</c> stays missing on a round trip.
    /// </summary>
    private static void SetAttribute(XElement e, string name, string value)
    {
        foreach (var a in e.Attributes())
        {
            if (!string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) continue;
            a.Value = Clean(value);
            return;
        }
        if (value.Length > 0) e.SetAttributeValue(name, Clean(value));
    }

    private static void Unknown(XmlWriter w, List<XElement> elements)
    {
        foreach (var e in elements) e.WriteTo(w);
    }

    private static void Opt(XmlWriter w, string name, string? value)
    {
        if (value is not null) Elem(w, name, value);
    }

    private static void Opt(XmlWriter w, string name, int? value)
    {
        if (value is { } v) Elem(w, name, I(v));
    }

    private static void Opt(XmlWriter w, string name, bool? value)
    {
        if (value is { } v) Elem(w, name, v ? "true" : "false");
    }

    private static void OptAttr(XmlWriter w, string name, string? value)
    {
        if (value is not null) Attr(w, name, value);
    }

    private static void Attr(XmlWriter w, string name, string value) => w.WriteAttributeString(name, Clean(value));

    private static void Elem(XmlWriter w, string name, string value) => w.WriteElementString(name, Clean(value));

    private static void Text(XmlWriter w, string value) => w.WriteString(Clean(value));

    /// <summary>
    /// Drops characters XML 1.0 cannot carry (control characters, lone surrogates, U+FFFE/U+FFFF) so that a stray
    /// character in a scene name never makes an export fail. Returns the string itself when it is clean.
    /// </summary>
    internal static string Clean(string value)
    {
        int i = 0;
        for (; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            if (!XmlConvert.IsXmlChar(c)) break;
        }
        if (i == value.Length) return value;

        var sb = new StringBuilder(value.Length);
        sb.Append(value, 0, i);
        for (; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    internal static string Id(Guid id) => id.ToString("D").ToUpperInvariant();

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string F(float v) => v == 0 ? "0" : v.ToString("R", CultureInfo.InvariantCulture);

    private static string V(Vector3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";
}
