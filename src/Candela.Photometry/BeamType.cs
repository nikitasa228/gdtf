namespace Candela.Photometry;

/// <summary>GDTF beam types (GDTF 1.2, DIN SPEC 15800:2022, Beam geometry attribute <c>BeamType</c>).</summary>
public enum BeamType
{
    /// <summary>Soft-edged beam (the GDTF default).</summary>
    Wash,

    /// <summary>Sharp-edged beam from an imaging (gate + lens) optic.</summary>
    Spot,

    /// <summary>No projected beam; the geometry glows by itself. Modelled as a diffuse (Lambertian) emitter.</summary>
    None,

    /// <summary>Rectangular (pyramidal) beam, e.g. panels and cyc lights; the radial profile is spot-like, the shape comes from the renderer's cookie.</summary>
    Rectangle,

    /// <summary>Plano-convex lens: fairly hard centre with noticeable spill.</summary>
    PC,

    /// <summary>Fresnel lens: soft beam with a long, smooth fall-off.</summary>
    Fresnel,

    /// <summary>Self-luminous glowing geometry: a diffuse emitter with a cosine (Lambertian) distribution.</summary>
    Glow,
}

/// <summary>Parsing of GDTF <c>BeamType</c> attribute values.</summary>
public static class BeamTypes
{
    /// <summary>
    /// Parses a GDTF BeamType value case-insensitively; null, empty or unknown values give <see cref="BeamType.Wash"/>,
    /// the GDTF default.
    /// </summary>
    public static BeamType Parse(string? value) => TryParse(value, out var type) ? type : BeamType.Wash;

    /// <summary>Parses a GDTF BeamType value case-insensitively.</summary>
    /// <returns>False for null, empty, numeric or unknown values.</returns>
    public static bool TryParse(string? value, out BeamType type)
    {
        type = BeamType.Wash;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length == 0 || char.IsDigit(trimmed[0]) || trimmed[0] == '-')
            return false;
        return Enum.TryParse(trimmed, ignoreCase: true, out type) && IsKnown(type);
    }

    /// <summary>
    /// True for the named <see cref="BeamType"/> values. A plain range check: unlike <see cref="Enum.IsDefined{TEnum}(TEnum)"/>
    /// it never touches the reflection cache, so per-frame code (<see cref="BeamDistribution.Create"/>) cannot allocate
    /// when the runtime has trimmed that cache.
    /// </summary>
    public static bool IsKnown(BeamType type) => (uint)type <= (uint)BeamType.Glow;
}
