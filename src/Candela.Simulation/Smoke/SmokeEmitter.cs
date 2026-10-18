using System.Numerics;

namespace Candela.Simulation;

/// <summary>What a <see cref="SmokeEmitter"/> does to the air.</summary>
public enum SmokeEmitterKind
{
    /// <summary>Haze machine: a fine aerosol at low exit velocity and almost neutral buoyancy; fills a room slowly and evenly.</summary>
    Hazer,

    /// <summary>Fog machine: a dense, warm jet (or a chilled, sinking one in low-fog mode).</summary>
    FogMachine,

    /// <summary>Effect fan / wind machine: adds a turbulent round-jet velocity field; emits no smoke.</summary>
    Fan,

    /// <summary>Generic hot (or cold) smoke source with explicit parameters, e.g. the plume above a flame.</summary>
    Plume,
}

/// <summary>
/// One smoke, haze or air source for a single simulation step, built by the engine from a fixture's position, aim and
/// DMX-evaluated output. Physical parameters describe the real nozzle; the simulation converts them to its cell size
/// conserving momentum and buoyancy flux (see <c>docs/simulation.md</c>). Use the factory methods for the documented
/// defaults and <c>with</c> to override single values (e.g. a measured mass rate).
/// </summary>
public readonly record struct SmokeEmitter
{
    /// <summary>Aerosol produced per joule of fog-machine heater power, kg/J (see <see cref="FogMachine"/>).</summary>
    public const float FogAerosolMassPerJoule = 0.5f / 1.5e6f;

    /// <summary>Default fog-machine heater power, W (typical 1.5 kW fogger; the median of the FOG units in <c>_specs</c>).</summary>
    public const float DefaultFogHeaterPower = 1500f;

    /// <summary>Default hazer aerosol output at full level, kg/s (0.04 g/s).</summary>
    public const float DefaultHazerMassRate = 4e-5f;

    /// <summary>Default effect-fan outlet diameter, m.</summary>
    public const float DefaultFanDiameter = 0.4f;

    /// <summary>Default effect-fan outlet speed at full level, m/s.</summary>
    public const float DefaultFanSpeed = 10f;

    /// <summary>Source type.</summary>
    public SmokeEmitterKind Kind { get; init; }

    /// <summary>World position of the nozzle (or fan centre), m.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Direction the nozzle points to (normalised internally; a zero or invalid vector means straight up, +Z).</summary>
    public Vector3 Direction { get; init; }

    /// <summary>Output level 0..1 from DMX: scales the mass rate (smoke) or the outlet speed (fans).</summary>
    public float Output { get; init; }

    /// <summary>Diameter of the nozzle or outlet, m.</summary>
    public float NozzleDiameter { get; init; }

    /// <summary>Speed of the jet at the nozzle at full output, m/s.</summary>
    public float ExitSpeed { get; init; }

    /// <summary>Aerosol mass produced per second at full output, kg/s (converted to density with the mass scattering coefficient).</summary>
    public float MassRate { get; init; }

    /// <summary>Temperature of the jet at the nozzle relative to ambient, K (negative = chilled).</summary>
    public float TemperatureExcess { get; init; }

    /// <summary>
    /// Whether the exit speed also scales with <see cref="Output"/>. True for fans; false for smoke sources, whose pumps
    /// pulse fluid into a nozzle that keeps its speed while the mass rate follows the output.
    /// </summary>
    public bool SpeedFollowsOutput { get; init; }

    /// <summary>
    /// A haze machine. Defaults: 80 mm fan outlet at 3 m/s, +2 K (water-based hazers warm the fluid slightly; oil
    /// crackers emit at ambient), 0.04 g/s aerosol at full output — touring hazers are specified at roughly 3–8 h per
    /// litre of fluid at continuous full output, about half of which ends up as long-lived droplets.
    /// </summary>
    /// <param name="position">Nozzle position, m.</param>
    /// <param name="direction">Nozzle direction.</param>
    /// <param name="output">Output level 0..1.</param>
    public static SmokeEmitter Hazer(Vector3 position, Vector3 direction, float output) => new()
    {
        Kind = SmokeEmitterKind.Hazer,
        Position = position,
        Direction = direction,
        Output = output,
        NozzleDiameter = 0.08f,
        ExitSpeed = 3f,
        MassRate = DefaultHazerMassRate,
        TemperatureExcess = 2f,
    };

    /// <summary>
    /// A fog machine. The aerosol mass rate follows from the heater power: vaporising a glycol/water fluid takes about
    /// 1.5 MJ/kg and roughly half of it (the glycol) condenses into visible droplets, so a 1.5 kW fogger makes 0.5 g/s
    /// (≈ 15 min per litre at continuous full output, in line with datasheets). Normal mode: an effective 20 mm
    /// nozzle at 10 m/s (momentum flux ≈ 0.04 N, which throws the fog 2–4 m) carrying half the heater power as heat
    /// (≈ +200 K at the nozzle for 1.5 kW). Low-fog mode: fog cooled with ice or CO₂ leaves a 150 mm duct at about
    /// 1 m/s and 15 K below ambient, so it sinks and spreads over the floor.
    /// </summary>
    /// <param name="position">Nozzle position, m.</param>
    /// <param name="direction">Nozzle direction.</param>
    /// <param name="output">Output level 0..1.</param>
    /// <param name="heaterPowerW">Heater power, W (the <c>_specs</c> <c>powerConsumptionW</c> of the unit); invalid → 1500 W.</param>
    /// <param name="lowFog">Chilled low-fog mode.</param>
    public static SmokeEmitter FogMachine(Vector3 position, Vector3 direction, float output,
        float heaterPowerW = DefaultFogHeaterPower, bool lowFog = false)
    {
        float power = float.IsFinite(heaterPowerW) && heaterPowerW > 0f ? heaterPowerW : DefaultFogHeaterPower;
        if (lowFog)
        {
            return new SmokeEmitter
            {
                Kind = SmokeEmitterKind.FogMachine,
                Position = position,
                Direction = direction,
                Output = output,
                NozzleDiameter = 0.15f,
                ExitSpeed = 1f,
                MassRate = FogAerosolMassPerJoule * power,
                TemperatureExcess = -15f,
            };
        }

        const float diameter = 0.02f, speed = 10f;
        float area = MathF.PI * 0.25f * diameter * diameter;
        float heatFlux = 0.5f * power;
        return new SmokeEmitter
        {
            Kind = SmokeEmitterKind.FogMachine,
            Position = position,
            Direction = direction,
            Output = output,
            NozzleDiameter = diameter,
            ExitSpeed = speed,
            MassRate = FogAerosolMassPerJoule * power,
            TemperatureExcess = heatFlux / (SimulationConstants.AirDensity * SimulationConstants.AirSpecificHeat * speed * area),
        };
    }

    /// <summary>
    /// An effect fan (wind machine). Defaults: 400 mm outlet at 10 m/s at full speed (≈ 1.3 m³/s, typical of 400 mm
    /// stage fans). The far field follows the self-similar turbulent round jet.
    /// </summary>
    /// <param name="position">Outlet centre, m.</param>
    /// <param name="direction">Blowing direction.</param>
    /// <param name="speed">Fan speed 0..1.</param>
    /// <param name="diameter">Outlet diameter, m.</param>
    /// <param name="maxExitSpeed">Outlet air speed at full speed, m/s.</param>
    public static SmokeEmitter Fan(Vector3 position, Vector3 direction, float speed,
        float diameter = DefaultFanDiameter, float maxExitSpeed = DefaultFanSpeed) => new()
        {
            Kind = SmokeEmitterKind.Fan,
            Position = position,
            Direction = direction,
            Output = speed,
            NozzleDiameter = diameter,
            ExitSpeed = maxExitSpeed,
            SpeedFollowsOutput = true,
        };

    /// <summary>
    /// A generic plume source: a disc of <paramref name="radius"/> blowing <paramref name="speed"/> m/s of gas at
    /// <paramref name="temperatureExcess"/> K above ambient carrying <paramref name="massRate"/> kg/s of smoke. Used
    /// for the hot gases above flames.
    /// </summary>
    public static SmokeEmitter Plume(Vector3 position, Vector3 direction, float radius, float speed, float massRate,
        float temperatureExcess) => new()
        {
            Kind = SmokeEmitterKind.Plume,
            Position = position,
            Direction = direction,
            Output = 1f,
            NozzleDiameter = 2f * radius,
            ExitSpeed = speed,
            MassRate = massRate,
            TemperatureExcess = temperatureExcess,
        };
}
