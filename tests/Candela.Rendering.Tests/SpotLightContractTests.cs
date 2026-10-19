namespace Candela.Rendering.Tests;

/// <summary>
/// The <see cref="SpotLight"/> contract stays backward compatible: every field's default is the neutral choice, so a
/// producer that never sets a newer field (such as <see cref="SpotLight.NoVolumetricScattering"/>) keeps its old look.
/// </summary>
public sealed class SpotLightContractTests
{
    [Fact]
    public void A_light_that_does_not_ask_otherwise_scatters_in_haze()
    {
        var light = new SpotLight { PeakCandela = 1000f, BeamAngle = 0.3f, FieldAngle = 0.5f, Iris = 1f };
        Assert.False(light.NoVolumetricScattering);
        Assert.False(default(SpotLight).NoVolumetricScattering);
        Assert.False(light.Gobo.IsValid);

        var surfacesOnly = light with { NoVolumetricScattering = true };
        Assert.True(surfacesOnly.NoVolumetricScattering);
        Assert.Equal(light.PeakCandela, surfacesOnly.PeakCandela); // the flag changes nothing photometric
        Assert.NotEqual(light, surfacesOnly);
    }
}
