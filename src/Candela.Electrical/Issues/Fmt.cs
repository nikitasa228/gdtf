using System.Globalization;

namespace Candela.Electrical;

/// <summary>Invariant-culture number formatting for issue messages (units always written out).</summary>
internal static class Fmt
{
    public static string W(double watts) => string.Create(CultureInfo.InvariantCulture, $"{watts:0} W");

    public static string A(double amperes) => string.Create(CultureInfo.InvariantCulture, $"{amperes:0.0} A");

    public static string V(double volts) => string.Create(CultureInfo.InvariantCulture, $"{volts:0} V");

    public static string Hz(double hertz) => string.Create(CultureInfo.InvariantCulture, $"{hertz:0} Hz");

    public static string Kg(double kilograms) => string.Create(CultureInfo.InvariantCulture, $"{kilograms:0.0} kg");

    public static string M(double metres) => string.Create(CultureInfo.InvariantCulture, $"{metres:0.0} m");

    public static string Pct(double percent) => string.Create(CultureInfo.InvariantCulture, $"{percent:0} %");

    /// <summary>A current above <paramref name="limit"/>, with two decimals when one would print it equal to the limit.</summary>
    public static string A(double amperes, double limit) =>
        amperes > limit && Math.Round(amperes, 1) <= Math.Round(limit, 1) ? string.Create(CultureInfo.InvariantCulture, $"{amperes:0.00} A") : A(amperes);

    /// <summary>A mass above <paramref name="limit"/>, with two decimals when one would print it equal to the limit.</summary>
    public static string Kg(double kilograms, double limit) =>
        kilograms > limit && Math.Round(kilograms, 1) <= Math.Round(limit, 1) ? string.Create(CultureInfo.InvariantCulture, $"{kilograms:0.00} kg") : Kg(kilograms);

    /// <summary>A power above <paramref name="limit"/>, with one decimal when none would print it equal to the limit.</summary>
    public static string W(double watts, double limit) =>
        watts > limit && Math.Round(watts) <= Math.Round(limit) ? string.Create(CultureInfo.InvariantCulture, $"{watts:0.0} W") : W(watts);

    public static string Supply(float voltageV, float frequencyHz, bool dc) =>
        dc ? string.Create(CultureInfo.InvariantCulture, $"{voltageV:0} V DC") : string.Create(CultureInfo.InvariantCulture, $"{voltageV:0} V {frequencyHz:0} Hz");

    /// <summary>"a, b, c and 4 more" (at most <paramref name="max"/> names).</summary>
    public static string List(IEnumerable<string> names, int max = 5)
    {
        var all = names.ToList();
        if (all.Count <= max) return string.Join(", ", all);
        return string.Join(", ", all.Take(max)) + string.Create(CultureInfo.InvariantCulture, $" and {all.Count - max} more");
    }

    public static string Count(int n, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? singular : plural)}");
}
