namespace Candela.Simulation;

/// <summary>
/// A complete copy of a <see cref="SmokeSimulation"/>'s state — velocity, density, temperature, pressure (the solver's
/// warm start, so a restored run continues bit-identically), solid cells, time and step counters. Used to scrub a
/// timecode show: keep snapshots at key frames and re-simulate forward from the nearest one. A snapshot can be reused
/// with <see cref="SmokeSimulation.CaptureSnapshot(SmokeSimulationSnapshot)"/> so scrubbing does not allocate.
/// </summary>
public sealed class SmokeSimulationSnapshot
{
    internal SmokeSimulationSnapshot(int cells, int uFaces, int vFaces, int wFaces)
    {
        U = new float[uFaces];
        V = new float[vFaces];
        W = new float[wFaces];
        Density = new float[cells];
        Temperature = new float[cells];
        Pressure = new float[cells];
        Solid = new byte[cells];
    }

    /// <summary>Cells along X, Y and Z of the simulation the snapshot belongs to.</summary>
    public (int X, int Y, int Z) Dimensions { get; internal set; }

    /// <summary>Simulated time at capture, s.</summary>
    public double Time { get; internal set; }

    /// <summary>Fixed sub-steps run before capture.</summary>
    public long StepCount { get; internal set; }

    internal double Accumulator { get; set; }

    internal System.Numerics.Vector3 Airflow { get; set; }

    internal float[] U { get; }

    internal float[] V { get; }

    internal float[] W { get; }

    internal float[] Density { get; }

    internal float[] Temperature { get; }

    internal float[] Pressure { get; }

    internal byte[] Solid { get; }
}
