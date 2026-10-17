using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Gdtf;

/// <summary>A wheel (colour, gobo, prism, animation, effects …) with its slots in physical order.</summary>
public sealed class Wheel
{
    /// <summary>Unique name, referenced by channel functions.</summary>
    public string Name { get; set; } = "";

    /// <summary>Slots in wheel order; channel sets address them with a 1-based <c>WheelSlotIndex</c>.</summary>
    public List<WheelSlot> Slots { get; } = new();

    /// <summary>Slot by 1-based GDTF index (as used by <c>ChannelSet.WheelSlotIndex</c>), or null when out of range.</summary>
    public WheelSlot? GetSlot(int index) => index >= 1 && index <= Slots.Count ? Slots[index - 1] : null;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One slot of a wheel: colour, filter, gobo image, prism facets or animation system.</summary>
public sealed class WheelSlot
{
    /// <summary>Default slot colour: GDTF white point (x 0.3127, y 0.3290, Y 100).</summary>
    public static readonly CieColor DefaultColor = new(0.3127f, 0.3290f, 100f);

    /// <summary>Slot name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Owning wheel (set by the reader).</summary>
    public Wheel? Wheel { get; set; }

    /// <summary>1-based position on the wheel.</summary>
    public int Index { get; set; }

    /// <summary>Colour of the slot, CIE 1931 xyY (Y in percent transmission for colour filters).</summary>
    public CieColor Color { get; set; } = DefaultColor;

    /// <summary>Filter name as written (GDTF 1.1+), or null.</summary>
    public string? FilterName { get; set; }

    /// <summary>Resolved physical filter with spectral measurements.</summary>
    public Filter? Filter { get; set; }

    /// <summary>Media file name without extension as written (gobo/animation image in <c>wheels/</c>), or null.</summary>
    public string? MediaFileName { get; set; }

    /// <summary>
    /// Archive entry of the media file (e.g. <c>wheels/gobo1.png</c>) when the reader found it in the archive, else null.
    /// Texture key for renderers: <c>gdtf:{fixture file}#{MediaPath}</c>.
    /// </summary>
    public string? MediaPath { get; set; }

    /// <summary>Prism facets (only for prism slots).</summary>
    public List<PrismFacet> Facets { get; } = new();

    /// <summary>Animation system of animation wheel slots (GDTF 1.2), or null.</summary>
    public AnimationSystem? AnimationSystem { get; set; }

    /// <summary>True when the slot is a prism (has facets).</summary>
    public bool IsPrism => Facets.Count > 0;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A facet of a prism slot: its colour and its 2D placement in the beam.</summary>
/// <remarks>
/// GDTF writes the facet "Rotation" as three groups <c>{a,b,c}{d,e,f}{g,h,i}</c> that are a 2D homogeneous transform with
/// the translation in the third group (e.g. <c>{1,0,0}{0,1,0}{0.5,0,1}</c> shifts the facet image by half a beam radius
/// along X). <see cref="Transform"/> holds it in the System.Numerics row-vector convention: the first two groups are the
/// rows of the 2×2 rotation/scale part, the third group's x/y are the translation.
/// </remarks>
public sealed class PrismFacet
{
    /// <summary>Colour of the facet, CIE 1931 xyY.</summary>
    public CieColor Color { get; set; } = WheelSlot.DefaultColor;

    /// <summary>Facet transform in the beam plane (row vectors: <c>p' = p * Transform</c>), units of beam radius.</summary>
    public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;

    /// <summary>The nine numbers as written, row by row.</summary>
    public float[] RawRotation { get; set; } = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
}

/// <summary>Animation system of an animation-wheel slot (GDTF 1.2): spline through three points and the visible circle.</summary>
public sealed class AnimationSystem
{
    /// <summary>First spline point relative to the media file centre.</summary>
    public Vector2 P1 { get; set; }

    /// <summary>Second spline point.</summary>
    public Vector2 P2 { get; set; }

    /// <summary>Third spline point.</summary>
    public Vector2 P3 { get; set; }

    /// <summary>Radius of the circle of the media file that is visible in the beam (same units as the points).</summary>
    public float Radius { get; set; }
}
