using System.Xml;

namespace Candela.Gdtf;

/// <summary>
/// An <see cref="XmlReader"/> wrapper that hides elements nested deeper than a limit and stops after a maximum number of
/// elements, so that a hostile <c>description.xml</c> cannot make <see cref="System.Xml.Linq.XDocument"/> loading
/// super-linear or exhaust memory.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.Xml.Linq.XContainer"/> walks up to the root for every node it adds, so building a tree costs
/// O(depth²): 30 000 nested elements take seconds, while the underlying reader streams them in milliseconds. Elements
/// deeper than <see cref="MaxDepth"/> are skipped with the reader's own <see cref="XmlReader.Skip"/> (linear) and
/// counted in <see cref="SkippedElements"/>; after <see cref="MaxElements"/> elements the document is cut off
/// (<see cref="Truncated"/>), as if it ended there. Normal files (the library's deepest is 12 levels, the largest
/// has 74 000 elements) pass through unchanged at the cost of one virtual call per node.
/// </para>
/// <para>The wrapper owns the inner reader and disposes it.</para>
/// </remarks>
internal sealed class LimitedXmlReader : XmlReader
{
    private readonly XmlReader _inner;
    private bool _truncated;

    public LimitedXmlReader(XmlReader inner, int maxDepth, long maxElements)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxElements, 1);
        _inner = inner;
        MaxDepth = maxDepth;
        MaxElements = maxElements;
    }

    /// <summary>Deepest element depth that is passed through (the root element has depth 0).</summary>
    public int MaxDepth { get; }

    /// <summary>Number of elements after which the document is cut off.</summary>
    public long MaxElements { get; }

    /// <summary>Elements (with their content) skipped because they were nested deeper than <see cref="MaxDepth"/>.</summary>
    public long SkippedElements { get; private set; }

    /// <summary>Elements passed through so far.</summary>
    public long ElementCount { get; private set; }

    /// <summary>True when the document was cut off after <see cref="MaxElements"/> elements.</summary>
    public bool Truncated => _truncated;

    public override bool Read()
    {
        if (_truncated || !_inner.Read()) return false;
        while (_inner.NodeType == XmlNodeType.Element && _inner.Depth > MaxDepth)
        {
            SkippedElements++;
            _inner.Skip();
            if (_inner.EOF || _inner.ReadState != ReadState.Interactive) return false;
        }
        if (_inner.NodeType == XmlNodeType.Element && ++ElementCount > MaxElements)
        {
            _truncated = true;
            return false;
        }
        return true;
    }

    public override int AttributeCount => _inner.AttributeCount;
    public override string BaseURI => _inner.BaseURI;
    public override int Depth => _inner.Depth;
    public override bool EOF => _truncated || _inner.EOF;
    public override bool IsEmptyElement => _inner.IsEmptyElement;
    public override string LocalName => _inner.LocalName;
    public override string Name => _inner.Name;
    public override string NamespaceURI => _inner.NamespaceURI;
    public override XmlNameTable NameTable => _inner.NameTable;
    public override XmlNodeType NodeType => _truncated ? XmlNodeType.None : _inner.NodeType;
    public override string Prefix => _inner.Prefix;
    public override ReadState ReadState => _truncated ? ReadState.EndOfFile : _inner.ReadState;
    public override string Value => _inner.Value;
    public override bool HasValue => _inner.HasValue;
    public override bool IsDefault => _inner.IsDefault;
    public override XmlSpace XmlSpace => _inner.XmlSpace;
    public override string XmlLang => _inner.XmlLang;
    public override XmlReaderSettings? Settings => _inner.Settings;
    public override bool CanResolveEntity => _inner.CanResolveEntity;
    public override string GetAttribute(int i) => _inner.GetAttribute(i);
    public override string? GetAttribute(string name) => _inner.GetAttribute(name);
    public override string? GetAttribute(string name, string? namespaceURI) => _inner.GetAttribute(name, namespaceURI);
    public override string? LookupNamespace(string prefix) => _inner.LookupNamespace(prefix);
    public override void MoveToAttribute(int i) => _inner.MoveToAttribute(i);
    public override bool MoveToAttribute(string name) => _inner.MoveToAttribute(name);
    public override bool MoveToAttribute(string name, string? ns) => _inner.MoveToAttribute(name, ns);
    public override bool MoveToElement() => _inner.MoveToElement();
    public override bool MoveToFirstAttribute() => _inner.MoveToFirstAttribute();
    public override bool MoveToNextAttribute() => _inner.MoveToNextAttribute();
    public override bool ReadAttributeValue() => _inner.ReadAttributeValue();
    public override void ResolveEntity() => _inner.ResolveEntity();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
