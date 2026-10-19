using System.Globalization;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Gdtf;
using Candela.Mvr;

namespace Candela.Cli;

/// <summary>Options of <c>candela info</c>.</summary>
internal sealed class InfoOptions
{
    public string File { get; private set; } = "";
    public List<string> Libraries { get; } = new();

    public static InfoOptions Parse(string[] args)
    {
        var o = new InfoOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (name == "--library") o.Libraries.Add(CliArgs.Value(args, ref i, name));
            else if (name.StartsWith('-')) throw new UsageException($"Unknown option '{name}'.");
            else if (o.File.Length > 0) throw new UsageException("info takes one file.");
            else o.File = name;
        }
        if (o.File.Length == 0) throw new UsageException("info needs a file (.gdtf, .mvr or .candela).");
        return o;
    }
}

/// <summary><c>candela info</c>: a summary of a GDTF fixture, an MVR scene or a Candela project.</summary>
internal static class InfoCommand
{
    public static int Run(InfoOptions options, TextWriter output, TextWriter error)
    {
        if (!File.Exists(options.File))
        {
            error.WriteLine($"'{options.File}' does not exist.");
            return Program.ExitFailure;
        }
        string ext = Path.GetExtension(options.File).ToLowerInvariant();
        return ext switch
        {
            ".gdtf" => Gdtf(options, output, error),
            StageEngine.MvrExtension => Mvr(options, output, error),
            StageEngine.ProjectExtension => Project(options, output, error),
            _ => Unknown(options.File, error),
        };
    }

    private static int Unknown(string file, TextWriter error)
    {
        error.WriteLine($"'{Path.GetFileName(file)}' is not a GDTF, MVR or Candela project file.");
        return Program.ExitFailure;
    }

    private static int Gdtf(InfoOptions options, TextWriter output, TextWriter error)
    {
        if (!GdtfReader.TryReadFile(options.File, out var type, out var problem) || type is null)
        {
            error.WriteLine($"'{Path.GetFileName(options.File)}' cannot be read: {problem}");
            return Program.ExitFailure;
        }
        var engineOptions = CliArgs.EngineOptions(options.Libraries, null);
        FixtureSpecs? specs = null;
        // The researched data the engine would use (settings, next to a library folder, installed copy, repository).
        if (engineOptions.SpecsIndexPath is { } specsPath)
        {
            try
            {
                specs = FixtureSpecsIndex.Load(specsPath).Find(Path.GetFileName(options.File));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                error.WriteLine($"The researched fixture data '{specsPath}' cannot be read: {e.Message}");
            }
        }

        output.WriteLine($"Fixture:      {type.Manufacturer} {type.LongName.Trim() switch { "" => type.Name, var l => l }}");
        output.WriteLine($"Name:         {type.Name} (short '{type.ShortName}')");
        output.WriteLine($"Type id:      {type.FixtureTypeId}");
        output.WriteLine($"GDTF:         DataVersion {type.DataVersion}, {type.Revisions.Count} revision(s)");
        if (specs is not null)
        {
            output.WriteLine($"Category:     {specs.Category.DisplayName()}{(specs.SubType is { } s ? $" ({s})" : "")}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Specs:        {(specs.PowerConsumptionW is { } w ? $"{w:0} W" : "power ?")}, {(specs.WeightKg is { } kg ? $"{kg:0.#} kg" : "weight ?")}, " +
                $"{(specs.MaxLuminousIntensityCd is { } cd ? $"{cd:0} cd" : "candela ?")}, {(specs.LuminousFluxLm is { } lm ? $"{lm:0} lm" : "flux ?")}, " +
                $"{(specs.NoiseLevelDb is { } db ? $"{db:0.#} dB(A)" : "noise ?")}"));
        }
        output.WriteLine($"Geometry:     {type.AllGeometries().Count()} geometries, {type.Models.Count} models, {type.Wheels.Count} wheels, " +
                         $"{type.PhysicalDescriptions.Emitters.Count} emitters");
        var props = type.PhysicalDescriptions.Properties;
        if (props.Weight is { } weight) output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Weight:       {weight:0.##} kg (GDTF)"));
        output.WriteLine($"DMX modes:    {type.DmxModes.Count}");
        foreach (var mode in type.DmxModes)
        {
            string breaks = mode.BreakFootprints.Count > 1
                ? " (" + string.Join(", ", mode.BreakFootprints.OrderBy(b => b.Key).Select(b => $"break {b.Key}: {b.Value}")) + ")"
                : "";
            output.WriteLine($"  {mode.Name,-32} {mode.Footprint,4} channels{breaks}");
        }
        foreach (var wheel in type.Wheels.Take(12))
            output.WriteLine($"  wheel {wheel.Name,-26} {wheel.Slots.Count,3} slots{(wheel.Slots.Any(s => s.MediaPath is not null) ? ", images" : "")}{(wheel.Slots.Any(s => s.IsPrism) ? ", prism" : "")}");
        if (type.Warnings.Count > 0)
        {
            output.WriteLine($"Warnings:     {type.Warnings.Count}");
            foreach (var w in type.Warnings.Take(10)) output.WriteLine($"  {w}");
        }
        return Program.ExitOk;
    }

    private static int Mvr(InfoOptions options, TextWriter output, TextWriter error)
    {
        using var engine = new StageEngine(CliArgs.EngineOptions(options.Libraries, null));
        MvrImportResult result;
        try
        {
            result = MvrImporter.ImportFile(options.File, new MvrImportOptions { Library = engine.Library, ValidateGeometry = false });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            error.WriteLine($"'{Path.GetFileName(options.File)}' cannot be read: {e.Message}");
            return Program.ExitFailure;
        }
        var doc = result.Document;
        output.WriteLine($"MVR:          {Path.GetFileName(options.File)}, version {doc.VerMajor}.{doc.VerMinor}, written by {doc.Provider ?? "?"} {doc.ProviderVersion}");
        output.WriteLine($"Files:        {doc.Files.Count} embedded ({doc.Files.Count(f => f.Name.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase))} GDTF)");
        Scene(result.Scene, output);
        output.WriteLine($"GDTF specs:   {result.Gdtfs.Count}");
        foreach (var g in result.Gdtfs) output.WriteLine($"  {g.Describe()}");
        output.WriteLine($"Models:       {result.Assets.Count}");
        RenderCommand.PrintIssues(output, result.Issues, 20);
        return Program.ExitOk;
    }

    private static int Project(InfoOptions options, TextWriter output, TextWriter error)
    {
        using var engine = new StageEngine(CliArgs.EngineOptions(options.Libraries, null));
        var opened = engine.Open(options.File);
        if (!opened.Success)
        {
            error.WriteLine(opened.Summary);
            return Program.ExitFailure;
        }
        output.WriteLine($"Project:      {Path.GetFileName(options.File)}");
        Scene(engine.Scene, output);
        output.WriteLine($"Assets:       {engine.Assets.Count} ({string.Join(", ", engine.Assets.Entries.GroupBy(a => a.Kind).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"))})");
        RenderCommand.PrintIssues(output, opened.Issues, 20);
        return Program.ExitOk;
    }

    private static void Scene(Core.SceneGraph.Scene scene, TextWriter output)
    {
        var all = scene.All().ToList();
        output.WriteLine($"Scene:        '{scene.Name}', {all.Count} entities on {scene.Layers.Count} layer(s): " +
                         string.Join(", ", scene.Layers.Select(l => $"{l.Name} ({all.Count(e => e.LayerId == l.Id)})")));
        var counts = all.SelectMany(e => e.Components).GroupBy(c => c.GetType().Name.Replace("Component", "", StringComparison.Ordinal))
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        output.WriteLine($"Components:   {string.Join(", ", counts)}");
        var fixtures = scene.Query<FixtureComponent>().GroupBy(f => (f.Component.GdtfSpec, f.Component.GdtfMode))
            .OrderByDescending(g => g.Count()).ToList();
        output.WriteLine($"Fixtures:     {fixtures.Sum(g => g.Count())} of {fixtures.Count} type(s)");
        foreach (var g in fixtures) output.WriteLine($"  {g.Count(),4} × {g.Key.GdtfSpec} [{g.Key.GdtfMode ?? "first mode"}]");
    }
}
