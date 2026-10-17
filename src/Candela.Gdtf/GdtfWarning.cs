namespace Candela.Gdtf;

/// <summary>
/// A non-fatal problem found while reading a GDTF file or loading one of its models (unknown enum value, dangling
/// reference, unparsable number, missing model file …). The reader keeps going and records one of these instead of
/// throwing, because real-world files are messy and one bad element must not lose the whole fixture.
/// </summary>
/// <param name="Path">Where the problem is, e.g. <c>DMXModes/Mode 1/DMXChannel Head_Pan</c>.</param>
/// <param name="Message">What is wrong and what the reader did about it.</param>
public sealed record GdtfWarning(string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}

/// <summary>
/// Thrown only when a file cannot produce a fixture type at all: not a zip archive, no <c>description.xml</c>, XML that
/// is not well formed, or no <c>FixtureType</c> element. Everything else becomes a <see cref="GdtfWarning"/>.
/// </summary>
public sealed class GdtfFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public GdtfFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Collects warnings with a cap so a pathological file cannot produce millions of entries.</summary>
internal sealed class WarningSink
{
    private readonly List<GdtfWarning> _warnings;
    private readonly int _limit;
    private int _suppressed;

    public WarningSink(List<GdtfWarning> target, int limit = 500)
    {
        _warnings = target;
        _limit = limit;
    }

    public int Count => _warnings.Count + _suppressed;

    public void Add(string path, string message)
    {
        if (_warnings.Count < _limit) _warnings.Add(new GdtfWarning(path, message));
        else _suppressed++;
    }

    /// <summary>Appends a summary line when warnings were dropped.</summary>
    public void Complete()
    {
        if (_suppressed > 0)
            _warnings.Add(new GdtfWarning("", $"{_suppressed} further warning(s) suppressed."));
        _suppressed = 0;
    }
}
