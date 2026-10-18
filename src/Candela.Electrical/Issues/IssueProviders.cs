using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;

namespace Candela.Electrical;

/// <summary>
/// Issues of the power and data networks and of the cables themselves (see <see cref="IssueCodes"/>): unpowered
/// fixtures, overloaded breakers/links/inlets/cables/dimmers, voltage mismatches, phase imbalance, wrong or
/// adapter-needing connectors, fixtures without data, universe mismatches, Y-splits, long DMX chains and runs, loops.
/// Each refresh solves the scene anew (and leaves the result in <see cref="NetworkSolver.GetOrSolve"/>'s cache).
/// </summary>
public sealed class ElectricalIssueProvider : IIssueProvider
{
    /// <summary>Creates the provider over a solver.</summary>
    public ElectricalIssueProvider(NetworkSolver solver)
    {
        Solver = solver ?? throw new ArgumentNullException(nameof(solver));
    }

    /// <summary>The solver used.</summary>
    public NetworkSolver Solver { get; }

    /// <inheritdoc />
    public string Name => "Electrical (power and data)";

    /// <inheritdoc />
    public IEnumerable<Issue> Analyze(Scene scene) => Solver.Solve(scene).Issues;
}

/// <summary>
/// Rigging issues: fixtures above the floor without a mount, mounts pointing at nothing, truss and rigging point
/// overloads (fixture weights, clamps and cable weight), fixtures of unknown weight.
/// </summary>
public sealed class RiggingIssueProvider : IIssueProvider
{
    /// <summary>Creates the provider over an analyzer.</summary>
    public RiggingIssueProvider(RiggingAnalyzer analyzer)
    {
        Analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    /// <summary>The analyzer used.</summary>
    public RiggingAnalyzer Analyzer { get; }

    /// <inheritdoc />
    public string Name => "Rigging";

    /// <inheritdoc />
    public IEnumerable<Issue> Analyze(Scene scene) => Analyzer.Analyze(scene).Issues;
}
