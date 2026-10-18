using Candela.Gdtf;

namespace Candela.Dmx;

/// <summary>State of one motor: its current position (control units) and velocity (units/s).</summary>
public struct MotorState
{
    /// <summary>Current position (degrees, slot position, 0 … 1 level …).</summary>
    public double Position;

    /// <summary>Current velocity, units/s (signed).</summary>
    public double Velocity;

    /// <summary>False until the first step; the first step jumps to the target (a fixture is powered at its DMX state).</summary>
    public bool Initialized;

    /// <summary>True while the motor has not settled on its target.</summary>
    public readonly bool IsMoving => Initialized && Velocity != 0.0;
}

/// <summary>
/// Physical smoothing of motorised attributes between DMX frames. Positions (pan, tilt, zoom, focus, iris, frost,
/// blades, wheels, mechanical colour flags) follow their DMX target with a time-optimal trapezoidal profile limited by
/// a maximum speed and acceleration; rotation speeds (endless pan/tilt, wheel and gobo spin) ramp with an angular
/// acceleration limit while their angle is integrated. Electronic attributes (LED intensity, colour mixing, strobe)
/// follow DMX instantly.
/// </summary>
/// <remarks>
/// <para><b>Exact and frame-rate independent.</b> Each step integrates the motion analytically through its constant-
/// acceleration phases (accelerate, cruise, brake), so evaluating 1 s in 60 steps or in 7 uneven steps gives the same
/// trajectory up to floating-point rounding. Nothing is random; all state is in <see cref="MotorState"/>.</para>
/// <para><b>Limits.</b> GDTF <c>RealFade</c> (time to travel PhysicalFrom → PhysicalTo, rest to rest) and
/// <c>RealAcceleration</c> (time to reach full speed) are used when a function provides them, so that a full-range move
/// takes exactly RealFade. Otherwise typical values of the fixture category apply, e.g. a moving head pans 540° in about
/// 2.5 s (257 °/s, 0.4 s to full speed), tilts 270° in about 1.7 s, zooms across its range in about 0.75 s and turns a
/// colour wheel once in about 0.9 s; scanner mirrors are ten times faster.</para>
/// </remarks>
public static class MotorDynamics
{
    /// <summary>
    /// Advances a position motor by <paramref name="dt"/> seconds toward <paramref name="target"/>. A non-initialised
    /// motor, instant limits or non-finite input jump straight to the target.
    /// </summary>
    public static void Step(ref MotorState state, double target, double dt, MotorLimits limits) => Step(ref state, target, dt, limits, 1f);

    /// <summary>
    /// <see cref="Step(ref MotorState, double, double, MotorLimits)"/> with the limits scaled by a speed factor (MSpeed
    /// channels): speed × <paramref name="speedFactor"/>, acceleration × <paramref name="speedFactor"/>² — without
    /// building scaled limits on the per-frame path.
    /// </summary>
    internal static void Step(ref MotorState state, double target, double dt, in MotorLimits limits, float speedFactor)
    {
        if (!double.IsFinite(target)) return;
        if (!state.Initialized || limits.IsInstant || !double.IsFinite(state.Position) || !double.IsFinite(state.Velocity))
        {
            state.Position = target;
            state.Velocity = 0;
            state.Initialized = true;
            return;
        }
        if (!(dt > 0)) return;
        double x = state.Position, v = state.Velocity;
        if (x == target && v == 0) return; // at rest on the target
        double f = speedFactor > 0f && float.IsFinite(speedFactor) ? speedFactor : 1.0;
        double vmax = limits.MaxSpeed > 0f ? limits.MaxSpeed * f : 1e-6;
        double amax = limits.MaxAcceleration * (f * f);
        double eps = 1e-7 * Math.Max(1.0, Math.Abs(target));

        if (!double.IsFinite(amax) || amax <= 0)
        {
            // pure slew-rate limit
            double e = target - x;
            double stepMax = vmax * dt;
            if (Math.Abs(e) <= stepMax) { x = target; v = 0; }
            else { x += Math.Sign(e) * stepMax; v = Math.Sign(e) * vmax; }
            state.Position = x;
            state.Velocity = v;
            return;
        }

        double invA = f == 1.0 ? limits.InverseAcceleration : limits.InverseAcceleration / (f * f);
        double settleSpeed2 = 2 * amax * eps + 1e-24; // squared speed below which the motor stops within eps
        double remaining = dt;
        for (int iter = 0; iter < 16 && remaining > 0; iter++)
        {
            double e = target - x;
            if (Math.Abs(e) <= eps && v * v <= settleSpeed2)
            {
                x = target;
                v = 0;
                break;
            }
            double dir = e > 0 ? 1 : e < 0 ? -1 : -Math.Sign(v);
            double u = v * dir; // speed toward the target
            double d = Math.Abs(e);
            double t, a; // phase duration and acceleration toward the target
            double brake = u > 0 ? 0.5 * u * u * invA : 0;
            if (u < 0)
            {
                t = -u * invA; // moving away: turn around
                a = amax;
            }
            else if (brake >= d - eps)
            {
                t = u * invA; // on (or past) the braking curve
                a = -amax;
                if (t <= 0) { x = target; v = 0; break; }
            }
            else if (u > vmax * (1 + 1e-9))
            {
                // faster than allowed (speed limit just lowered): slow down, but never past the braking curve
                t = (u - vmax) * invA;
                a = -amax;
            }
            else if (u < vmax * (1 - 1e-9))
            {
                double t1 = (vmax - u) * invA;
                double t2 = (Math.Sqrt(u * u / 2 + amax * d) - u) * invA;
                t = Math.Min(t1, t2);
                a = amax;
            }
            else
            {
                t = (d - brake) / u; // cruise until the braking curve
                a = 0;
            }
            if (!(t > 1e-12)) t = 1e-12;
            double tt = Math.Min(t, remaining);
            double accel = a * dir;
            x += (v + 0.5 * accel * tt) * tt;
            v += accel * tt;
            remaining -= tt;
        }
        if (Math.Abs(target - x) <= eps && v * v <= settleSpeed2)
        {
            x = target;
            v = 0;
        }
        state.Position = x;
        state.Velocity = v;
    }

    /// <summary>
    /// Advances a rotation: the speed (units/s, kept in <see cref="MotorState.Velocity"/>) ramps toward
    /// <paramref name="targetSpeed"/> with <paramref name="acceleration"/> (units/s², ∞ = instant) and the angle (in
    /// <see cref="MotorState.Position"/>) integrates the speed exactly. The angle is kept within ±360·10⁶ by whole turns.
    /// </summary>
    public static void StepRotation(ref MotorState state, double targetSpeed, double dt, double acceleration)
    {
        if (!double.IsFinite(targetSpeed)) targetSpeed = 0;
        if (!state.Initialized)
        {
            state.Initialized = true;
            state.Velocity = targetSpeed;
            if (!double.IsFinite(state.Position)) state.Position = 0;
            return;
        }
        if (!(dt > 0)) return;
        double s = state.Velocity;
        double angle = state.Position;
        if (!double.IsFinite(acceleration) || acceleration <= 0)
        {
            angle += targetSpeed * dt;
            s = targetSpeed;
        }
        else
        {
            double diff = targetSpeed - s;
            double tr = Math.Abs(diff) / acceleration;
            double a = Math.Sign(diff) * acceleration;
            if (tr >= dt)
            {
                angle += s * dt + 0.5 * a * dt * dt;
                s += a * dt;
            }
            else
            {
                angle += s * tr + 0.5 * a * tr * tr + targetSpeed * (dt - tr);
                s = targetSpeed;
            }
        }
        if (Math.Abs(angle) > 360e6) angle %= 360.0;
        state.Position = angle;
        state.Velocity = s;
    }

    /// <summary>
    /// How a channel function's control value moves and with which limits: from GDTF RealFade/RealAcceleration when
    /// given, otherwise typical values of the attribute and fixture category.
    /// </summary>
    /// <param name="function">The compiled function.</param>
    /// <param name="category">Fixture category (scanners move mirrors much faster than moving heads).</param>
    /// <param name="wheelSlots">Slot count of the function's wheel (0 when none).</param>
    /// <param name="mechanicalColor">True for lamp fixtures whose CMY/CTO are mechanical flags (LED engines mix electronically).</param>
    public static (MotionKind Kind, MotorLimits Limits) DefaultMotion(ProfileFunction function, FixtureCategory category, int wheelSlots,
        bool mechanicalColor)
    {
        ArgumentNullException.ThrowIfNull(function);
        var kind = function.Class.Kind;
        float range = MathF.Abs(function.PhysicalTo - function.PhysicalFrom);
        bool scanner = category == FixtureCategory.Scanner;

        // continuous rotations: ramp of the speed (deg/s²)
        switch (kind)
        {
            case AttributeKind.PanRotate or AttributeKind.TiltRotate:
                return (MotionKind.Rotation, new MotorLimits(float.PositiveInfinity, scanner ? 3600f : 360f));
            case AttributeKind.WheelSpin or AttributeKind.SlotRotate or AttributeKind.WheelSelectSpin:
                return (MotionKind.Rotation, new MotorLimits(float.PositiveInfinity, 1080f));
        }

        (float speed, float accelTime) typical;
        switch (kind)
        {
            case AttributeKind.Pan:
                typical = scanner ? (2000f, 0.05f) : (257f, 0.4f);
                break;
            case AttributeKind.Tilt:
                typical = scanner ? (2000f, 0.05f) : (200f, 0.33f);
                break;
            case AttributeKind.Rotation:
                typical = (180f, 0.4f);
                break;
            case AttributeKind.Translation:
                typical = (Math.Max(range, 1f) / 2f, 0.4f);
                break;
            case AttributeKind.Zoom:
                typical = (Math.Max(range, 1f) / 0.6f, 0.15f);
                break;
            case AttributeKind.Focus or AttributeKind.FocusDistance:
                typical = (Math.Max(range, 0.01f) / 0.5f, 0.1f);
                break;
            case AttributeKind.Iris:
                typical = (Math.Max(range, 0.01f) / 0.25f, 0.05f);
                break;
            case AttributeKind.Frost:
                typical = (Math.Max(range, 0.01f) / 0.3f, 0.05f);
                break;
            case AttributeKind.BladeA or AttributeKind.BladeB or AttributeKind.BladeSoft:
                typical = (Math.Max(range, 0.01f) / 0.35f, 0.08f);
                break;
            case AttributeKind.BladeRotation:
                typical = (Math.Max(range, 1f) / 0.5f, 0.1f);
                break;
            case AttributeKind.ShaperRotation:
                typical = (Math.Max(range, 1f) / 0.8f, 0.15f);
                break;
            case AttributeKind.AnimationInsertion:
                typical = (Math.Max(range, 0.01f) / 0.4f, 0.08f);
                break;
            case AttributeKind.WheelIndex:
                typical = (450f, 0.15f);
                break;
            case AttributeKind.SlotIndex:
                typical = (360f, 0.15f);
                break;
            case AttributeKind.WheelSelect or AttributeKind.WheelSelectShake when function.UsesSlotPosition:
                {
                    float slots = Math.Max(wheelSlots, 2);
                    float fullTurn = function.Class.Wheel == WheelFamily.Prism ? 0.5f : 0.8f;
                    typical = (slots / fullTurn, 0.1f);
                    break;
                }
            case AttributeKind.ColorSub or AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc when mechanicalColor:
                typical = (Math.Max(range, 0.01f) / 0.35f, 0.08f);
                break;
            default:
                return (MotionKind.None, MotorLimits.Instant);
        }

        float speedLimit = typical.speed;
        float accel = typical.speed / typical.accelTime;
        float fade = function.RealFade;
        if (!function.UsesSlotPosition && range > 0f && fade > 0.01f && float.IsFinite(fade))
        {
            // RealFade is the time to travel PhysicalFrom → PhysicalTo from rest to rest, RealAcceleration the time to
            // reach full speed. A trapezoidal profile covers range = v·(fade − ta); when fade < 2·ta the motor never
            // reaches a cruise speed and the profile is a triangle peaking at 2·range/fade.
            float ta = function.RealAcceleration > 0.005f && float.IsFinite(function.RealAcceleration)
                ? function.RealAcceleration
                : Math.Min(typical.accelTime, fade / 2f);
            if (fade >= 2f * ta)
            {
                speedLimit = range / (fade - ta);
                accel = speedLimit / ta;
            }
            else
            {
                accel = 4f * range / (fade * fade);
                speedLimit = accel * fade / 2f;
            }
        }
        return (MotionKind.Position, new MotorLimits(speedLimit, accel));
    }
}
