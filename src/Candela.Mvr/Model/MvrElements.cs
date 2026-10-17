using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Candela.Core.SceneGraph.Components;

namespace Candela.Mvr;

/// <summary>A geometry entry of <c>Geometries</c> or of a <c>Symdef</c>: a 3D file or a symbol instance.</summary>
public abstract class MvrGeometry
{
    /// <summary>Transform relative to the owning object (or symbol) in metres; identity when absent.</summary>
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    /// <summary>True when the file wrote a <c>Matrix</c>.</summary>
    public bool HasMatrix { get; set; }

    /// <summary>Child elements this reader does not know, kept verbatim.</summary>
    public List<XElement> UnknownElements { get; } = new();
}

/// <summary><c>Geometry3D</c>: a 3D file of the archive (3DS in millimetres, glTF/GLB in metres).</summary>
public sealed class MvrGeometry3D : MvrGeometry
{
    /// <summary>Archive file name including extension (<c>fileName</c>).</summary>
    public string FileName { get; set; } = "";

    /// <inheritdoc />
    public override string ToString() => FileName;
}

/// <summary><c>Symbol</c>: an instance of a <see cref="MvrSymdef"/>.</summary>
public sealed class MvrSymbol : MvrGeometry
{
    /// <summary>Identity of the instance (<c>uuid</c>).</summary>
    public Guid Uuid { get; set; }

    /// <summary>The instanced definition (<c>symdef</c>).</summary>
    public Guid SymdefId { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"Symbol {SymdefId}";
}

/// <summary>
/// One DMX address of an object (<c>Addresses/Address</c>). MVR writes either the absolute address
/// (<c>(universe − 1) · 512 + address</c>) or <c>universe.address</c>; <see cref="Break"/> is the 0-based index of the
/// GDTF DMX break the address patches (GDTF <c>DMXBreak</c> = <see cref="Break"/> + 1).
/// </summary>
public sealed class MvrAddress
{
    /// <summary>Creates an address from an absolute value (1 … 2³¹ − 1) and a 0-based break.</summary>
    public MvrAddress(int absolute, int dmxBreak = 0, string? rawValue = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(absolute, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(dmxBreak);
        Absolute = absolute;
        Break = dmxBreak;
        RawValue = rawValue;
    }

    /// <summary>0-based DMX break (<c>break</c>); 0 for the first (often only) break.</summary>
    public int Break { get; }

    /// <summary>Absolute address: <c>(universe − 1) · 512 + address</c>, 1-based.</summary>
    public int Absolute { get; }

    /// <summary>The value exactly as written in the file (null when built in code).</summary>
    public string? RawValue { get; }

    /// <summary>1-based universe.</summary>
    public int Universe => (Absolute - 1) / 512 + 1;

    /// <summary>1-based address within the universe (1 … 512).</summary>
    public int Address => (Absolute - 1) % 512 + 1;

    /// <summary>The Core patch entry: universe, address and the 1-based GDTF break.</summary>
    public DmxAddress ToDmxAddress() => new(Universe, Address, Break + 1);

    /// <summary>Creates the MVR address of a Core patch entry (Core breaks are 1-based, MVR breaks 0-based).</summary>
    public static MvrAddress FromDmxAddress(DmxAddress address) =>
        new(address.Absolute, System.Math.Max(0, address.Break - 1));

    /// <summary>
    /// Parses an address value: an absolute number (<c>"741"</c>) or <c>"universe.address"</c> (<c>"2.229"</c>,
    /// address 1 … 512). Returns false for anything else, zero or negative values.
    /// </summary>
    public static bool TryParse(string? text, int dmxBreak, out MvrAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text) || dmxBreak < 0) return false;
        string t = text.Trim();
        int dot = t.IndexOf('.');
        if (dot < 0)
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int abs) || abs < 1) return false;
            address = new MvrAddress(abs, dmxBreak, text);
            return true;
        }
        if (!int.TryParse(t.AsSpan(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out int universe) ||
            !int.TryParse(t.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int addr) ||
            universe < 1 || addr < 1 || addr > 512 || universe > (int.MaxValue - 512) / 512)
        {
            return false;
        }
        address = new MvrAddress((universe - 1) * 512 + addr, dmxBreak, text);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Universe}.{Address:D3} (break {Break})";
}

/// <summary>Network configuration of a device port (<c>Addresses/Network</c>, MVR 1.6).</summary>
public sealed class MvrNetwork
{
    /// <summary>GDTF geometry (wiring object) the setting applies to.</summary>
    public string Geometry { get; set; } = "";

    /// <summary>IPv4 address, or null.</summary>
    public string? IPv4 { get; set; }

    /// <summary>IPv4 subnet mask, or null.</summary>
    public string? SubnetMask { get; set; }

    /// <summary>IPv6 address, or null.</summary>
    public string? IPv6 { get; set; }

    /// <summary>DHCP enabled (<c>dhcp="on"</c>), or null when not written.</summary>
    public bool? Dhcp { get; set; }

    /// <summary>Host name, or null.</summary>
    public string? Hostname { get; set; }
}

/// <summary>A control protocol of a device (<c>Protocols/Protocol</c>, MVR 1.6).</summary>
public sealed class MvrProtocol
{
    /// <summary>GDTF geometry (interface) the protocol runs on, or null.</summary>
    public string? Geometry { get; set; }

    /// <summary>Custom name, or null.</summary>
    public string? Name { get; set; }

    /// <summary>Protocol (e.g. "Art-Net", "sACN", "PosiStageNet"), or null.</summary>
    public string? Type { get; set; }

    /// <summary>Protocol version, or null.</summary>
    public string? Version { get; set; }

    /// <summary>Transmission ("Unicast", "Multicast", "Broadcast", "Anycast"), or null.</summary>
    public string? Transmission { get; set; }
}

/// <summary>Beam alignment of a fixture geometry (<c>Alignments/Alignment</c>).</summary>
public sealed class MvrAlignment
{
    /// <summary>GDTF geometry (beam) aligned, or null for the default beam.</summary>
    public string? Geometry { get; set; }

    /// <summary>Up vector (default 0, 0, 1).</summary>
    public Vector3 Up { get; set; } = Vector3.UnitZ;

    /// <summary>Beam direction (default 0, 0, −1).</summary>
    public Vector3 Direction { get; set; } = -Vector3.UnitZ;
}

/// <summary>Replaces a GDTF wheel slot / emitter / filter by another one (<c>Overwrites/Overwrite</c>).</summary>
public sealed class MvrOverwrite
{
    /// <summary>Node link to the universal (MVR-wide) definition.</summary>
    public string Universal { get; set; } = "";

    /// <summary>Node link to the element of the fixture's GDTF that is replaced (empty = none).</summary>
    public string Target { get; set; } = "";
}

/// <summary>A cable connection between two objects' wiring geometries (<c>Connections/Connection</c>, MVR 1.6).</summary>
public sealed class MvrConnection
{
    /// <summary>Wiring object (port) of the object that holds the connection.</summary>
    public string Own { get; set; } = "";

    /// <summary>Wiring object (port) of the other object.</summary>
    public string Other { get; set; } = "";

    /// <summary>Uuid of the other object.</summary>
    public Guid ToObject { get; set; }
}

/// <summary>A video mapping of a fixture (<c>Mappings/Mapping</c>): where the mapping definition's pixels land.</summary>
public sealed class MvrMapping
{
    /// <summary>The mapping definition (<c>linkedDef</c>).</summary>
    public Guid LinkedDefinition { get; set; }

    /// <summary>Horizontal size in pixels (<c>ux</c>), or null.</summary>
    public int? Ux { get; set; }

    /// <summary>Vertical size in pixels (<c>uy</c>), or null.</summary>
    public int? Uy { get; set; }

    /// <summary>Horizontal offset in pixels (<c>ox</c>), or null.</summary>
    public int? Ox { get; set; }

    /// <summary>Vertical offset in pixels (<c>oy</c>), or null.</summary>
    public int? Oy { get; set; }

    /// <summary>Rotation about the screen normal in degrees (<c>rz</c>), or null.</summary>
    public float? Rz { get; set; }
}

/// <summary>A custom gobo image of a fixture (<c>Gobo</c>).</summary>
public sealed class MvrGobo
{
    /// <summary>Image file in the archive.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Rotation in degrees (<c>rotation</c>).</summary>
    public float Rotation { get; set; }
}

/// <summary>A video source (<c>Source</c>).</summary>
public sealed class MvrSource
{
    /// <summary>GDTF geometry (display) that shows the source (<c>linkedGeometry</c>).</summary>
    public string LinkedGeometry { get; set; } = "";

    /// <summary>Source type: "NDI", "File", "CITP" or "CaptureDevice" (<c>type</c>).</summary>
    public string Type { get; set; } = "File";

    /// <summary>Source value: file name, stream name, … (element text).</summary>
    public string Value { get; set; } = "";
}

/// <summary>How video content is fitted to its target (<c>ScaleHandeling</c>, spelt as in the spec).</summary>
public enum MvrScaleHandling
{
    /// <summary>Scale keeping the aspect ratio.</summary>
    ScaleKeepRatio,

    /// <summary>Scale to fill, ignoring the aspect ratio.</summary>
    ScaleIgnoreRatio,

    /// <summary>Keep the pixel size and centre the content.</summary>
    KeepSizeCenter,
}

/// <summary>What a projector shows (<c>Projections/Projection</c>).</summary>
public sealed class MvrProjection
{
    /// <summary>Video source, or null.</summary>
    public MvrSource? Source { get; set; }

    /// <summary>Fitting, or null when not written.</summary>
    public MvrScaleHandling? ScaleHandling { get; set; }
}
