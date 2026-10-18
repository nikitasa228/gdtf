using System.Globalization;
using System.Numerics;
using Candela.Core.Settings;
using Candela.Engine;

namespace Candela.Cli;

/// <summary>Small helpers shared by the commands: option values, numbers, vectors, and creating the engine.</summary>
internal static class CliArgs
{
    /// <summary>Reads the value after option <paramref name="name"/> at <paramref name="i"/> (advancing it).</summary>
    public static string Value(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length) throw new UsageException($"Option {name} needs a value.");
        return args[++i];
    }

    public static float Number(string name, string value, float min, float max)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.IsFinite(x) || x < min || x > max)
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"{name} must be a number between {min} and {max} (got '{value}')."));
        return x;
    }

    public static int Integer(string name, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            throw new UsageException($"{name} must be an integer between {min} and {max} (got '{value}').");
        return n;
    }

    /// <summary>Parses "x,y,z" (metres).</summary>
    public static Vector3 Vector(string name, string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) throw new UsageException($"{name} must be x,y,z (got '{value}').");
        return new Vector3(Number(name, parts[0], -1e5f, 1e5f), Number(name, parts[1], -1e5f, 1e5f), Number(name, parts[2], -1e5f, 1e5f));
    }

    /// <summary>Parses universe lists such as "1-4", "1,3,5" or "1-2,7" (1 … 63999).</summary>
    public static IReadOnlyList<int> Universes(string name, string value)
    {
        var result = new SortedSet<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = part.IndexOf('-', 1);
            if (dash > 0)
            {
                int a = Integer(name, part[..dash], 1, 63999), b = Integer(name, part[(dash + 1)..], 1, 63999);
                if (b < a) (a, b) = (b, a);
                if (b - a > 1024) throw new UsageException($"{name}: at most 1024 universes per range.");
                for (int u = a; u <= b; u++) result.Add(u);
            }
            else
            {
                result.Add(Integer(name, part, 1, 63999));
            }
        }
        if (result.Count == 0) throw new UsageException($"{name} needs at least one universe.");
        return result.ToList();
    }

    /// <summary>Engine options from the user's settings, with the command-line overrides.</summary>
    public static StageEngineOptions EngineOptions(IReadOnlyList<string> libraries, string? cache)
    {
        var options = StageEngineOptions.FromSettings(AppSettings.Load());
        if (libraries.Count > 0) options = options with { LibraryFolders = libraries.ToList() };
        if (!string.IsNullOrWhiteSpace(cache)) options = options with { CacheDirectory = cache };
        return options;
    }

    /// <summary>Formats a number with the invariant culture.</summary>
    public static string F(double value, string format = "0.##") => value.ToString(format, CultureInfo.InvariantCulture);
}
