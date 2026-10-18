namespace Candela.Dmx.Tests;

/// <summary>Motor dynamics: default and GDTF speed limits, limits respected, frame-rate independence, rotations.</summary>
public class MotorDynamicsTests
{
    private const string DefaultHead = "ACME@ACME_AECO_20_(AECO_20)"; // no RealFade in its GDTF
    private const string RobeT1 = "Robe_Lighting@Robin_T1_Profile@"; // RealFade/RealAcceleration on pan and tilt
    private const string Viper = "Martin_Professional@MAC_Viper_Profile";

    private static ProfileFunction Function(FixtureProfile p, string attribute) =>
        p.Functions.First(f => f.Attribute == attribute && f.Motion == MotionKind.Position);

    /// <summary>Time for a motor at rest at <paramref name="from"/> to settle at <paramref name="to"/>, stepping by <paramref name="dt"/>.</summary>
    private static double TravelTime(MotorLimits limits, double from, double to, double dt)
    {
        var m = new MotorState();
        MotorDynamics.Step(ref m, from, dt, limits); // power-up: jumps to the first target
        for (int i = 1; i < 1_000_000; i++)
        {
            MotorDynamics.Step(ref m, to, dt, limits);
            if (m.Position == to && m.Velocity == 0) return i * dt;
        }
        return double.PositiveInfinity;
    }

    [Fact]
    public void Default_moving_head_pans_540_degrees_in_about_two_and_a_half_seconds()
    {
        var p = TestData.Profile(DefaultHead);
        var pan = Function(p, "Pan");
        var tilt = Function(p, "Tilt");
        Assert.Equal(0f, pan.RealFade);
        Assert.Equal(540f, MathF.Abs(pan.PhysicalTo - pan.PhysicalFrom));
        double panTime = TravelTime(pan.Limits, pan.PhysicalFrom, pan.PhysicalTo, 1e-3);
        Assert.InRange(panTime, 2.3, 2.7);
        double tiltTime = TravelTime(tilt.Limits, tilt.PhysicalFrom, tilt.PhysicalTo, 1e-3);
        Assert.InRange(tiltTime, 1.0, 2.0);
        // LED engine: intensity and colour mixing are electronic
        foreach (var f in p.Functions.Where(f => f.Class.Kind is AttributeKind.Dimmer or AttributeKind.ColorAdd or AttributeKind.Strobe))
            Assert.Equal(MotionKind.None, f.Motion);
    }

    [Theory]
    [InlineData(RobeT1, "Pan")]
    [InlineData(RobeT1, "Tilt")]
    [InlineData(Viper, "Pan")] // RealFade < 2 × RealAcceleration: triangular profile
    [InlineData(Viper, "Zoom")]
    public void Gdtf_real_fade_is_the_full_range_travel_time(string fixture, string attribute)
    {
        var p = TestData.Profile(fixture, fixture == Viper ? "Basic" : null);
        var f = Function(p, attribute);
        Assert.True(f.RealFade > 0f);
        double t = TravelTime(f.Limits, f.PhysicalFrom, f.PhysicalTo, 1e-4);
        Assert.Equal(f.RealFade, t, 0.002);
        if (f.RealAcceleration > 0f && f.RealFade >= 2f * f.RealAcceleration)
            Assert.Equal(f.RealAcceleration, f.Limits.MaxSpeed / f.Limits.MaxAcceleration, 1e-3f);
    }

    [Fact]
    public void Speed_and_acceleration_stay_within_the_limits_without_overshoot()
    {
        var limits = new MotorLimits(200f, 500f);
        const double dt = 1e-3;
        var m = new MotorState();
        MotorDynamics.Step(ref m, 0, dt, limits);
        double previousV = 0, maxV = 0, maxA = 0, maxX = 0;
        for (int i = 0; i < 4000; i++)
        {
            double target = i < 2000 ? 300 : 120; // reverse half-way
            MotorDynamics.Step(ref m, target, dt, limits);
            maxV = Math.Max(maxV, Math.Abs(m.Velocity));
            maxA = Math.Max(maxA, Math.Abs(m.Velocity - previousV) / dt);
            maxX = Math.Max(maxX, m.Position);
            previousV = m.Velocity;
            if (i == 1999) Assert.Equal(300, m.Position, 1e-9); // settled exactly
        }
        Assert.Equal(120, m.Position, 1e-9);
        Assert.Equal(0, m.Velocity);
        Assert.True(maxV <= 200 * (1 + 1e-9), $"speed {maxV}");
        Assert.True(maxA <= 500 * (1 + 1e-6), $"acceleration {maxA}");
        Assert.True(maxX <= 300 + 1e-9, $"overshoot to {maxX}");
    }

    [Fact]
    public void Motion_is_independent_of_the_frame_rate()
    {
        var limits = new MotorLimits(257f, 640f);
        // targets change at these times (frames always land on them, as DMX changes arrive between frames)
        (double Time, double Target)[] program = [(0, 0), (0.05, 270), (0.9, -100), (1.35, 40), (1.6, 40)];

        static double Run(MotorLimits limits, (double Time, double Target)[] program, Func<int, double> stepOf)
        {
            var m = new MotorState();
            MotorDynamics.Step(ref m, program[0].Target, 0, limits);
            int k = 0;
            for (int seg = 0; seg < program.Length - 1; seg++)
            {
                double t = program[seg].Time, end = program[seg + 1].Time;
                while (t < end - 1e-12)
                {
                    double dt = Math.Min(stepOf(k++), end - t);
                    MotorDynamics.Step(ref m, program[seg].Target, dt, limits);
                    t += dt;
                }
            }
            return m.Position;
        }

        double fine = Run(limits, program, _ => 1.0 / 240.0);
        double coarse = Run(limits, program, _ => 1.0 / 24.0);
        var rng = new Random(7);
        double uneven = Run(limits, program, _ => 0.001 + rng.NextDouble() * 0.08);
        Assert.Equal(fine, coarse, 1e-6);
        Assert.Equal(fine, uneven, 1e-6);
        Assert.NotEqual(40, fine, 1e-3); // still moving at the end: the comparison is not trivially at rest
    }

    [Fact]
    public void Rotation_speed_ramps_and_the_angle_is_integrated_exactly()
    {
        var one = new MotorState();
        MotorDynamics.StepRotation(ref one, 0, 0, 1080); // at rest
        MotorDynamics.StepRotation(ref one, 360, 1.0, 1080);
        Assert.Equal(360, one.Velocity, 1e-9);
        Assert.Equal(0.5 * 1080 * (1.0 / 3) * (1.0 / 3) + 360 * (2.0 / 3), one.Position, 1e-6); // 60° ramp + 240° cruise

        var many = new MotorState();
        MotorDynamics.StepRotation(ref many, 0, 0, 1080);
        for (int i = 0; i < 37; i++) MotorDynamics.StepRotation(ref many, 360, 1.0 / 37, 1080);
        Assert.Equal(one.Position, many.Position, 1e-6);

        var instant = new MotorState();
        MotorDynamics.StepRotation(ref instant, 0, 0, double.PositiveInfinity);
        MotorDynamics.StepRotation(ref instant, -90, 2.0, double.PositiveInfinity);
        Assert.Equal(-180, instant.Position, 1e-9);
    }

    [Fact]
    public void The_evaluator_moves_pan_gradually_and_identically_at_any_frame_rate()
    {
        var p = TestData.Profile(Viper, "Basic");
        var pan = Function(p, "Pan");
        int channel = p.FindChannels("Pan")[0];
        int axis = p.Channels[channel].Axis;

        float[] Run(double fps)
        {
            var state = new FixtureState(p);
            var buffer = new FixtureDmxBuffer(p);
            DmxEncoder.Encode(buffer, "Pan", 0f);
            FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: true); // power-up at the centre
            DmxEncoder.Encode(buffer, "Pan", pan.PhysicalTo);
            var samples = new List<float>();
            int frames = (int)Math.Round(5 * fps);
            for (int i = 1; i <= frames; i++)
            {
                FixtureEvaluator.Evaluate(state, buffer, i / fps, null, applyDynamics: true);
                if (i % (int)Math.Round(fps / 4) == 0) samples.Add(state.Axes[axis].Pan); // every 0.25 s
            }
            return samples.ToArray();
        }

        var at60 = Run(60);
        var at25 = Run(24);
        Assert.Equal(at60.Length, at25.Length);
        for (int i = 0; i < at60.Length; i++) Assert.Equal(at60[i], at25[i], 1e-4f);
        float target = pan.PhysicalTo * MathF.PI / 180f;
        Assert.InRange(MathF.Abs(at60[0]), 0.01f, MathF.Abs(target) * 0.5f); // 0.25 s: on its way
        for (int i = 1; i < at60.Length; i++) Assert.True(MathF.Abs(at60[i]) >= MathF.Abs(at60[i - 1]) - 1e-6f); // monotonic
        Assert.Equal(target, at60[^1], 1e-5f); // 5 s: arrived
    }

    [Fact]
    public void Motor_speed_channels_scale_the_motion()
    {
        var p = TestData.Profile(RobeT1);
        var mspeed = p.Functions.FirstOrDefault(f => f.Class.Kind == AttributeKind.MotionSpeed && f.SpeedGroup is 0 or -1 && !f.IsConstant);
        Assert.NotNull(mspeed);
        int channel = p.FindChannels("Pan")[0];
        int axis = p.Channels[channel].Axis;
        float PanAfter(float speedFraction)
        {
            var state = new FixtureState(p);
            var buffer = new FixtureDmxBuffer(p);
            DmxEncoder.EncodeFunction(buffer, mspeed!.Index, mspeed.PhysicalFrom + (mspeed.PhysicalTo - mspeed.PhysicalFrom) * speedFraction);
            DmxEncoder.Encode(buffer, "Pan", 0f);
            FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: true);
            DmxEncoder.Encode(buffer, "Pan", 270f);
            for (int i = 1; i <= 30; i++) FixtureEvaluator.Evaluate(state, buffer, i / 60.0, null, applyDynamics: true);
            return MathF.Abs(state.Axes[axis].Pan);
        }
        float fast = PanAfter(0f), slow = PanAfter(1f);
        Assert.True(fast > 0f && slow > 0f);
        Assert.True(MathF.Abs(fast - slow) > 0.05f * MathF.Max(fast, slow), $"fast {fast} slow {slow}");
    }
}
