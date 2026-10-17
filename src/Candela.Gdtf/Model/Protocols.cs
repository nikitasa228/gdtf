namespace Candela.Gdtf;

/// <summary>Protocol information of a fixture type: RDM plus the generic protocol blocks.</summary>
public sealed class GdtfProtocols
{
    /// <summary>RDM descriptions (<c>FTRDM</c>).</summary>
    public List<RdmInfo> Rdm { get; } = new();

    /// <summary>Art-Net, sACN, PosiStageNet, OpenSoundControl, CITP and unknown protocol blocks.</summary>
    public List<ProtocolBlock> Others { get; } = new();
}

/// <summary>RDM identification of the device (ESTA manufacturer id and device model id) and its personalities.</summary>
public sealed class RdmInfo
{
    /// <summary>ESTA manufacturer id (16 bit).</summary>
    public uint ManufacturerId { get; set; }

    /// <summary>RDM device model id (16 bit).</summary>
    public uint DeviceModelId { get; set; }

    /// <summary>Software versions with their DMX personalities.</summary>
    public List<RdmSoftwareVersion> SoftwareVersions { get; } = new();
}

/// <summary>An RDM software version and the personalities it offers.</summary>
public sealed class RdmSoftwareVersion
{
    /// <summary>Software version id.</summary>
    public uint Value { get; set; }

    /// <summary>Personalities (RDM personality number → DMX mode).</summary>
    public List<RdmPersonality> Personalities { get; } = new();
}

/// <summary>An RDM DMX personality mapped to a DMX mode.</summary>
public sealed class RdmPersonality
{
    /// <summary>RDM personality number.</summary>
    public uint Value { get; set; }

    /// <summary>DMX mode name as written.</summary>
    public string DmxModeName { get; set; } = "";

    /// <summary>Resolved DMX mode.</summary>
    public DmxMode? DmxMode { get; set; }
}

/// <summary>A protocol block other than RDM (Art-Net, sACN, PosiStageNet, OpenSoundControl, CITP), kept generically.</summary>
public sealed class ProtocolBlock
{
    /// <summary>XML element name (e.g. <c>Art-Net</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Attributes of the block element.</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>Child entries (e.g. Art-Net <c>Map Key="…" Value="…"</c>), each as element name plus attributes.</summary>
    public List<(string Element, Dictionary<string, string> Attributes)> Entries { get; } = new();
}
