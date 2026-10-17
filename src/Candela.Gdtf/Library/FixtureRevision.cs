using System.Globalization;
using System.Text.RegularExpressions;

namespace Candela.Gdtf;

/// <summary>
/// What the revision part of a GDTF file name (<c>Manufacturer@Model@Revision.gdtf</c>) says about its age: a date
/// and/or a version number. GDTF Share and manufacturers write it freely ("2026-07-21_Color_control_channel_revision",
/// "20260620", "V1.81_Corrected_Factory_Tilt", "Rev_002_-_All_Modes", "25-31-10_Release", "rev101_RELEASE"), so both
/// are best-effort and either can be null.
/// </summary>
public sealed partial class FixtureRevision : IComparable<FixtureRevision>
{
    private FixtureRevision(string text, DateOnly? date, Version? version, DateTime fileTimeUtc)
    {
        Text = text;
        Date = date;
        Version = version;
        FileTimeUtc = fileTimeUtc;
    }

    /// <summary>Revision text as written in the file name (empty when the name has no revision part).</summary>
    public string Text { get; }

    /// <summary>Date found in the text, or null.</summary>
    public DateOnly? Date { get; }

    /// <summary>Version number found in the text ("V1.81", "Rev_002", "rev101"), or null.</summary>
    public Version? Version { get; }

    /// <summary>Last write time of the file (UTC), the tie breaker when the texts say nothing.</summary>
    public DateTime FileTimeUtc { get; }

    /// <summary>Parses a revision text; <paramref name="fileTimeUtc"/> is the file's last write time.</summary>
    public static FixtureRevision Parse(string? text, DateTime fileTimeUtc = default)
    {
        text ??= "";
        return new FixtureRevision(text, FindDate(text), FindVersion(text), fileTimeUtc);
    }

    /// <summary>
    /// Orders by date, then version, then file time, then text (ordinal), each missing value counting as oldest — a
    /// total order, so "newest" is well defined even for names in different styles.
    /// </summary>
    public int CompareTo(FixtureRevision? other)
    {
        if (other is null) return 1;
        int c = Nullable.Compare(Date, other.Date);
        if (c != 0) return c;
        c = Comparer<Version?>.Default.Compare(Version, other.Version);
        if (c != 0) return c;
        c = FileTimeUtc.CompareTo(other.FileTimeUtc);
        return c != 0 ? c : string.CompareOrdinal(Text, other.Text);
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    /// <summary>
    /// Finds a date written as year-month-day with 4- or 2-digit year and '-', '_', '.' or no separators. Some
    /// exporters write year-day-month ("2024-21-04"); when the middle number cannot be a month and the last can, the
    /// two are swapped.
    /// </summary>
    internal static DateOnly? FindDate(string text)
    {
        // Four-digit years first, so "Firmware_1_00_2026_07_12" is not read as 2000-…; candidates may overlap.
        foreach (var pattern in new[] { LongYearDatePattern(), ShortYearDatePattern() })
        {
            for (int start = 0; start < text.Length;)
            {
                var m = pattern.Match(text, start);
                if (!m.Success) break;
                int year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (year < 100) year += 2000;
                int a = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
                int b = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
                if (year is >= 2000 and <= 2100)
                {
                    if (a is >= 1 and <= 12 && b >= 1 && b <= DateTime.DaysInMonth(year, a)) return new DateOnly(year, a, b);
                    if (b is >= 1 and <= 12 && a >= 1 && a <= DateTime.DaysInMonth(year, b)) return new DateOnly(year, b, a);
                }
                start = m.Index + 1;
            }
        }
        return null;
    }

    internal static Version? FindVersion(string text)
    {
        var m = VersionPattern().Match(text);
        if (!m.Success) return null;
        var parts = m.Groups["v"].Value.Split('.', '_');
        Span<int> n = stackalloc int[4];
        int count = 0;
        foreach (var p in parts)
        {
            if (count == 4) break;
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) break;
            n[count++] = value;
        }
        return count switch
        {
            0 => null,
            1 => new Version(n[0], 0),
            2 => new Version(n[0], n[1]),
            3 => new Version(n[0], n[1], n[2]),
            _ => new Version(n[0], n[1], n[2], n[3]),
        };
    }

    [GeneratedRegex(@"(?<!\d)(?<y>20\d{2})[-_.]?(?<a>\d{2})[-_.]?(?<b>\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LongYearDatePattern();

    [GeneratedRegex(@"(?<!\d)(?<y>\d{2})[-_.](?<a>\d{2})[-_.](?<b>\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortYearDatePattern();

    [GeneratedRegex(@"(?:^|[^a-z])(?:v|ver|version|rev|revision)[ _.\-]?(?<v>\d+(?:[._]\d+){0,3})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}
