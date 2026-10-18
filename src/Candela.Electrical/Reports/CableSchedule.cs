namespace Candela.Electrical;

/// <summary>One end of a cable in the schedule.</summary>
/// <param name="EntityId">Device entity.</param>
/// <param name="EntityName">Device name (null when the entity is gone).</param>
/// <param name="PortName">Port.</param>
/// <param name="Connector">Port connector display name (null when the port is unknown).</param>
public sealed record CableEnd(Guid EntityId, string? EntityName, string PortName, string? Connector);

/// <summary>One cable of the schedule.</summary>
public sealed record CableScheduleRow
{
    /// <summary>Cable entity.</summary>
    public required Guid CableEntityId { get; init; }

    /// <summary>Label (or entity name).</summary>
    public required string Label { get; init; }

    /// <summary>Type id.</summary>
    public required string TypeId { get; init; }

    /// <summary>Type name (the id when the type is unknown).</summary>
    public required string TypeName { get; init; }

    /// <summary>Signal (null when the type is unknown).</summary>
    public SignalKind? Signal { get; init; }

    /// <summary>Cut length in metres (routed × slack).</summary>
    public required float LengthM { get; init; }

    /// <summary>Routed length in metres.</summary>
    public required float RoutedLengthM { get; init; }

    /// <summary>Weight in kg.</summary>
    public required float WeightKg { get; init; }

    /// <summary>Source end (outlet, console output, link output), or the first end when no direction is known.</summary>
    public CableEnd? From { get; init; }

    /// <summary>The other ends.</summary>
    public required IReadOnlyList<CableEnd> To { get; init; }

    /// <summary>Peak current in A of a power cable, when powered.</summary>
    public double? CurrentA { get; init; }

    /// <summary>Power through a power cable in W, when powered.</summary>
    public double? LoadW { get; init; }
}

/// <summary>Length and weight totals of one cable type.</summary>
/// <param name="TypeId">Type id.</param>
/// <param name="TypeName">Type name.</param>
/// <param name="Count">Number of cables.</param>
/// <param name="LengthM">Total cut length in metres.</param>
/// <param name="WeightKg">Total weight in kg.</param>
public sealed record CableTypeTotal(string TypeId, string TypeName, int Count, float LengthM, float WeightKg);

/// <summary>Cable schedule for the UI and paperwork: every cable with label, type, length, weight and its ends.</summary>
public sealed class CableSchedule
{
    private CableSchedule(IReadOnlyList<CableScheduleRow> rows)
    {
        Rows = rows;
        Totals = rows.GroupBy(r => r.TypeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CableTypeTotal(g.Key, g.First().TypeName, g.Count(), g.Sum(r => r.LengthM), g.Sum(r => r.WeightKg)))
            .OrderBy(t => t.TypeId, StringComparer.OrdinalIgnoreCase).ToList();
        TotalLengthM = rows.Sum(r => r.LengthM);
        TotalWeightKg = rows.Sum(r => r.WeightKg);
    }

    /// <summary>Cables in scene order.</summary>
    public IReadOnlyList<CableScheduleRow> Rows { get; }

    /// <summary>Totals per type.</summary>
    public IReadOnlyList<CableTypeTotal> Totals { get; }

    /// <summary>Total cut length in metres.</summary>
    public float TotalLengthM { get; }

    /// <summary>Total weight in kg.</summary>
    public float TotalWeightKg { get; }

    /// <summary>Builds the schedule from a solution.</summary>
    public static CableSchedule Build(NetworkSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var rows = new List<CableScheduleRow>(solution.Cables.Count);
        foreach (var c in solution.Cables)
        {
            int fromIndex = c.UpstreamEndpointIndex is { } up
                ? c.Endpoints.ToList().FindIndex(e => e.Index == up)
                : c.Endpoints.ToList().FindIndex(e => e.Port is { Direction: PortDirection.Output });
            if (fromIndex < 0 && c.Endpoints.Count > 0) fromIndex = 0;
            CableEnd End(CableEndpointInfo e) => new(e.EntityId, e.EntityName, e.PortName, e.Port?.Connector.DisplayName);
            rows.Add(new CableScheduleRow
            {
                CableEntityId = c.EntityId,
                Label = c.DisplayLabel,
                TypeId = c.TypeId,
                TypeName = c.Type?.DisplayName ?? c.TypeId,
                Signal = c.Type?.Signal,
                LengthM = c.LengthM,
                RoutedLengthM = c.Geometry.RoutedLengthM,
                WeightKg = c.WeightKg,
                From = fromIndex >= 0 ? End(c.Endpoints[fromIndex]) : null,
                To = c.Endpoints.Where((_, i) => i != fromIndex).Select(End).ToList(),
                CurrentA = c.PeakCurrentA,
                LoadW = c.LoadW,
            });
        }
        return new CableSchedule(rows);
    }
}
