namespace Candela.Dmx.Tests;

/// <summary>
/// Manual values (<c>FixtureComponent.ManualAttributes</c>) and GDTF mode masters: a manual value must leave the fixture in
/// the same state as the same values sent over DMX (MAC Viper: Gobo1 is the mode master of the Gobo1Pos channel —
/// indexing in 0…34, rotation in 35…59).
/// </summary>
public class ManualOverrideTests
{
    private const string Viper = "Martin_Professional@MAC_Viper_Profile";
    private const float Deg = MathF.PI / 180f;

    private static FixtureProfile Profile => TestData.Profile(Viper, "Basic");

    private static FixtureDmxBuffer Open(FixtureProfile p)
    {
        var buffer = new FixtureDmxBuffer(p);
        DmxEncoder.Encode(buffer, "Shutter1", 1f);
        DmxEncoder.Encode(buffer, "Dimmer", 1f);
        return buffer;
    }

    private static string ActiveAttribute(FixtureProfile p, FixtureState s, string channelAttribute) =>
        p.Functions[s.GetChannel(p.FindChannels(channelAttribute)[0]).Function].Attribute;

    [Fact]
    public void A_manual_mode_master_re_decodes_the_channels_that_depend_on_it()
    {
        var p = Profile;
        int gobo = p.FindChannels("Gobo1")[0];
        // console: wheel in rotation mode, Gobo1Pos channel = rotation speed
        var buffer = Open(p);
        DmxEncoder.EncodeFunction(buffer, p.FunctionsOf(gobo)[1].Index, 3f);
        Assert.Equal(1, DmxEncoder.Encode(buffer, "Gobo1PosRotate", 200f));
        var dmx = FixtureEvaluator.Evaluate(p, buffer, 1.0);
        Assert.Equal("Gobo1PosRotate", ActiveAttribute(p, dmx, "Gobo1Pos"));
        Assert.Equal(200f * Deg, dmx.Beams[0].Gobos[0].RotationSpeed, 0.01f);

        // a manual Gobo1 = 3 selects the indexing mode: the Gobo1Pos channel now indexes (as over DMX), it does not spin
        var manual = FixtureEvaluator.Evaluate(p, buffer, 1.0, new Dictionary<string, float> { ["Gobo1"] = 3f });
        var reference = buffer;
        DmxEncoder.Encode(reference, "Gobo1", 3f);
        var encoded = FixtureEvaluator.Evaluate(p, reference, 1.0);
        Assert.Equal("Gobo1Pos", ActiveAttribute(p, encoded, "Gobo1Pos"));
        Assert.Equal("Gobo1Pos", ActiveAttribute(p, manual, "Gobo1Pos"));
        Assert.Equal(0f, manual.Beams[0].Gobos[0].RotationSpeed);
        Assert.Equal(encoded.Beams[0].Gobos[0].Rotation, manual.Beams[0].Gobos[0].Rotation, 1e-4f);
        Assert.Equal(3, manual.Beams[0].Gobos[0].Slot);
    }

    [Fact]
    public void A_manual_function_moves_its_mode_master_into_range()
    {
        var p = Profile;
        int gobo = p.FindChannels("Gobo1")[0];
        var rotation = p.FunctionsOf(gobo)[1];
        // from the GDTF defaults (indexing mode): a manual rotation speed puts the wheel into a rotation mode
        var state = FixtureEvaluator.Evaluate(p, Open(p), 1.0, new Dictionary<string, float> { ["Gobo1PosRotate"] = 150f });
        var g = state.GetChannel(gobo);
        Assert.InRange(g.Dmx, rotation.DmxFrom, rotation.DmxTo);
        Assert.Equal("Gobo1PosRotate", ActiveAttribute(p, state, "Gobo1Pos"));
        Assert.Equal(150f * Deg, state.Beams[0].Gobos[0].RotationSpeed, 0.01f);

        // with a manual slot as well, the slot is selected in the rotation mode (not in the indexing mode)
        state = FixtureEvaluator.Evaluate(p, Open(p), 1.0, new Dictionary<string, float> { ["Gobo1PosRotate"] = 150f, ["Gobo1"] = 4f });
        Assert.Equal(rotation.Index, state.GetChannel(gobo).Function);
        Assert.Equal(4, state.Beams[0].Gobos[0].Slot);
        Assert.Equal(150f * Deg, state.Beams[0].Gobos[0].RotationSpeed, 0.01f);
    }

    [Fact]
    public void A_manual_index_angle_keeps_the_wheel_in_its_indexing_mode()
    {
        var p = Profile;
        var state = FixtureEvaluator.Evaluate(p, Open(p), 0, new Dictionary<string, float> { ["Gobo1"] = 2f, ["Gobo1Pos"] = 90f });
        Assert.Equal("Gobo1", ActiveAttribute(p, state, "Gobo1"));
        Assert.Equal(90f * Deg, state.Beams[0].Gobos[0].Rotation, 0.01f);
        Assert.Equal(0f, state.Beams[0].Gobos[0].RotationSpeed);
        Assert.Equal(2, state.Beams[0].Gobos[0].Slot);
    }
}
