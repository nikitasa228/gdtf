namespace Candela.Core.Math;

/// <summary>
/// Unit conversions used at file and UI boundaries. Inside the engine everything is SI: metres, kilograms,
/// seconds, watts, radians; photometry in candela / lux / lumen / cd·m⁻².
/// </summary>
public static class Units
{
    /// <summary>Degrees → radians factor.</summary>
    public const float DegToRad = MathF.PI / 180f;

    /// <summary>Radians → degrees factor.</summary>
    public const float RadToDeg = 180f / MathF.PI;

    /// <summary>MVR stores positions in millimetres.</summary>
    public const float MillimetresToMetres = 0.001f;

    /// <summary>Metres → feet factor (1 ft = 0.3048 m).</summary>
    public const float MetresToFeet = 3.280839895f;

    /// <summary>Pounds (avoirdupois) → kilograms factor.</summary>
    public const float PoundsToKilograms = 0.45359237f;

    /// <summary>1 foot-candle = 1 lumen per square foot.</summary>
    public const float LuxPerFootCandle = 10.7639104f;

    /// <summary>Converts degrees to radians.</summary>
    public static float Radians(float degrees) => degrees * DegToRad;

    /// <summary>Converts radians to degrees.</summary>
    public static float Degrees(float radians) => radians * RadToDeg;

    /// <summary>Exposure value at ISO 100 for a scene average luminance (cd/m²), with the usual K = 12.5.</summary>
    public static float Ev100FromLuminance(float luminance) => MathF.Log2(MathF.Max(luminance, 1e-6f) * 100f / 12.5f);

    /// <summary>Scale that maps scene luminance (cd/m²) to an exposed value of 1 for a given EV100.</summary>
    public static float ExposureFromEv100(float ev100) => 1f / (1.2f * MathF.Pow(2f, ev100));
}
