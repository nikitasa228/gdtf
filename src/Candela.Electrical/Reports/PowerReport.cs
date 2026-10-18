namespace Candela.Electrical;

/// <summary>One row of the power table: a circuit of an outlet of a source.</summary>
public sealed record PowerReportRow
{
    /// <summary>Source entity.</summary>
    public required Guid SourceEntityId { get; init; }

    /// <summary>Source name.</summary>
    public required string SourceName { get; init; }

    /// <summary>Outlet port name.</summary>
    public required string Outlet { get; init; }

    /// <summary>Circuit on the outlet (1; 1..6 for multicore outlets).</summary>
    public required int Circuit { get; init; }

    /// <summary>Phase 1..3, 0 for a three-phase outlet.</summary>
    public required int Phase { get; init; }

    /// <summary>Outlet connector.</summary>
    public required string Connector { get; init; }

    /// <summary>Voltage in V.</summary>
    public required float VoltageV { get; init; }

    /// <summary>Active power in W.</summary>
    public required double LoadW { get; init; }

    /// <summary>Apparent power in VA.</summary>
    public required double ApparentPowerVA { get; init; }

    /// <summary>Highest phase current in A.</summary>
    public required double CurrentA { get; init; }

    /// <summary>Breaker rating in A, or null.</summary>
    public float? BreakerA { get; init; }

    /// <summary>Current in percent of the breaker, or null.</summary>
    public double? PercentOfBreaker { get; init; }

    /// <summary>True when the breaker is exceeded.</summary>
    public required bool Overloaded { get; init; }

    /// <summary>Devices on the circuit, depth first.</summary>
    public required IReadOnlyList<Guid> Devices { get; init; }
}

/// <summary>Totals of one source for the power table.</summary>
public sealed record PowerReportSource
{
    /// <summary>Source entity.</summary>
    public required Guid SourceEntityId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Voltage (phase to neutral) in V.</summary>
    public required float VoltageV { get; init; }

    /// <summary>Frequency in Hz (0 = DC).</summary>
    public required float FrequencyHz { get; init; }

    /// <summary>Phases.</summary>
    public required int Phases { get; init; }

    /// <summary>True for a distro fed from another source.</summary>
    public required bool IsFed { get; init; }

    /// <summary>True when the source has power.</summary>
    public required bool IsPowered { get; init; }

    /// <summary>Total active power in W.</summary>
    public required double TotalW { get; init; }

    /// <summary>Current per phase L1..L3 in A.</summary>
    public required IReadOnlyList<double> PhaseCurrentsA { get; init; }

    /// <summary>Circuit rows.</summary>
    public required IReadOnlyList<PowerReportRow> Rows { get; init; }
}

/// <summary>Power table for the UI: per source, outlet and circuit — load W, current A, % of breaker, phase.</summary>
public sealed class PowerReport
{
    private PowerReport(IReadOnlyList<PowerReportSource> sources, IReadOnlyList<Guid> unpowered)
    {
        Sources = sources;
        Rows = sources.SelectMany(s => s.Rows).ToList();
        UnpoweredDevices = unpowered;
        TotalW = sources.Where(s => !s.IsFed).Sum(s => s.TotalW);
    }

    /// <summary>Sources in scene order.</summary>
    public IReadOnlyList<PowerReportSource> Sources { get; }

    /// <summary>All circuit rows.</summary>
    public IReadOnlyList<PowerReportRow> Rows { get; }

    /// <summary>Devices without power.</summary>
    public IReadOnlyList<Guid> UnpoweredDevices { get; }

    /// <summary>Total active power drawn from unfed sources (fed distros are inside their feeding circuit) in W.</summary>
    public double TotalW { get; }

    /// <summary>Builds the table from a solution.</summary>
    public static PowerReport Build(NetworkSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var sources = solution.PowerSources.Select(s => new PowerReportSource
        {
            SourceEntityId = s.SourceEntityId,
            Name = s.Name,
            VoltageV = s.VoltageV,
            FrequencyHz = s.FrequencyHz,
            Phases = s.Phases,
            IsFed = s.IsFed,
            IsPowered = s.IsPowered,
            TotalW = s.TotalW,
            PhaseCurrentsA = s.PhaseCurrentsA,
            Rows = s.Circuits.Select(c => new PowerReportRow
            {
                SourceEntityId = c.SourceEntityId,
                SourceName = c.SourceName,
                Outlet = c.Outlet.Name,
                Circuit = c.Circuit,
                Phase = c.Phase,
                Connector = c.Outlet.Connector.DisplayName,
                VoltageV = c.VoltageV,
                LoadW = c.LoadW,
                ApparentPowerVA = c.ApparentPowerVA,
                CurrentA = c.CurrentA,
                BreakerA = c.BreakerA,
                PercentOfBreaker = c.PercentOfBreaker,
                Overloaded = c.IsOverloaded,
                Devices = c.Feeds.Select(f => f.DeviceEntityId).ToList(),
            }).ToList(),
        }).ToList();
        return new PowerReport(sources, solution.UnpoweredDevices);
    }
}
