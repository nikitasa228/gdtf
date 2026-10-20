using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Candela.Gdtf;
using Xunit.Abstractions;

namespace Candela.Dmx.Tests;

/// <summary>
/// Gobos, prisms, animation wheels and beam shapers in and out of the beam: GDTF defaults and ranges named "Open",
/// "Removed", "Close", "No function" … leave them out, ramps move them in gradually, ranges that only index or rotate a
/// mechanism do not insert it, and a beam shaper stretches the beam.
/// </summary>
public partial class WheelInsertionTests(ITestOutputHelper output)
{
    private const string Rivale = "Ayrton@Rivale_Wash"; // AnimationWheel1 'open' 0, 'Animation 1' from 8; BeamShaper 'Open' 0, 'Beam Shaper' 128
    private const string Chiron = "ACME@ACME_CHIRON_(XP_380_BEAM_II)"; // Prism1 'Open' 0, 'Prism 1' from 8 (8 facets)
    private const string Picasso = "Elation@Artiste_Picasso"; // AnimationWheel1 'OPEN' 0, rotation from 8 on the same channel
    private const string Mondrian = "Elation@Artiste_Mondrian"; // AnimationWheel1 'Open' 0, 'Animation min to max' from 8
    private const string OronH2 = "Cameo@Oron_H2"; // AnimationWheel1 'Open' 0, '' 1 … 254, 'Animation In' 255
    private const string Esprite = "Robe_Lighting@Robin_Esprite@"; // 'No function', 'Effect wheel indexing', ramp cycles
    private const string FocusProfile = "ADJ@Focus_Profile"; // Prism1 'No Effect' 0 … 31, Prism1Pos index
    private const string PrSpot = "PRstage@SPOT_460_CMY"; // animation 'Open' selects 'Wheel Slot 1' (no media, the hole)

    /// <summary>
    /// Files whose GDTF defaults put a gobo, prism, animation wheel or beam shaper into the beam, and why that is the
    /// file's own choice (no out range exists, or the default value selects a slot). Every entry must still be hit, so
    /// the list cannot hide a regression elsewhere and is cleaned when a file changes.
    /// </summary>
    private static readonly Dictionary<string, string> InsertedAtDefaults = new(StringComparer.Ordinal)
    {
        ["ADJ@Hydro_Hybrid"] = "the gobo channel's default selects slot 'Beam'",
        ["BeamZ_Pro@Nereid_500_IP65"] = "the gobo channels' defaults select slot 2",
        ["Brighter@Pixel_Strobe"] = "a wheel-less animation channel without an out range",
        ["Chauvet_Professional@Maverick_Storm_4_Profile"] = "the animation wheel channel is 'Min' … 'Max' only (no out range)",
        ["Chauvet_Professional@Maverick_Storm_4_SoloWash"] = "the animation channel is 'Min' … 'Max' only (no out range)",
        ["Chauvet_Professional@Rogue_R3_Beam"] = "19Ch Mode: the prism channels' only function is 'Prism Insert' from DMX 5, their default",
        ["Cyclops_Lighting@Aquaray_500_SBW"] = "a wheel-less gobo channel whose default is 'Safe', not an out range",
        ["Elation@Fuze_Max_Spot"] = "the default DMX 8 is 'Animation Min to Max'",
        ["Elation@Platinum_FLX"] = "a wheel-less animation channel without an out range",
        ["Elation@Smarty_Hybrid"] = "the default DMX 22 is 'Gobo 1'",
        ["Flash@LED_MOVING_HEAD_19x15W_BIG_EYE"] = "a beam-shaper lens index channel without an out range",
        ["Infinity@Furion_H402_Hybrid"] = "the beam shaper ('DIFFUSER') default is its 'Min' range",
        ["MARK@MOVILED_SPOT_100"] = "the gobo channel's initial function is 'Index', default 'Gobo 1'",
        ["Robe_Lighting@Robin_Tarrantula"] = "the beam shaper channel defaults to 128 (shaper in)",
    };

    /// <summary>Range names that say the mechanism is out of the beam (the acceptance list: open, removed, close, off, no …).</summary>
    [GeneratedRegex(@"^\s*(open|removed?|close|closed|off|no ?function|no prism|no gobo|no animation|no effect)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OutName();

    /// <summary>
    /// Files in which selecting a slot that holds an item, with every other channel at its default, does not show that
    /// item, and why: another channel the file writes with the same attribute selects a different item at its default.
    /// </summary>
    private static readonly Dictionary<string, string> SelectionOverridden = new(StringComparer.Ordinal)
    {
        ["BeamZ@LCB_14_C@"] = "each zone's own Color1 channel (default 25, slot 2) colours its beam over the shared 'Effects1' programs",
        ["BeamZ_Pro@BAC330_LED_ProPAR_RGBW@"] = "'ColorMacro Select' (Color1 on the effects channel, default 128 = slot 18) is active in the 'Static' range too",
    };

    private sealed record ScanResult(
        IReadOnlyDictionary<string, string> InsertedAtDefaults, IReadOnlyList<string> OutFailures, int OutSetsChecked,
        IReadOnlyList<string> SlotItems, int Files, IReadOnlyList<string> LostSelections, int SelectionsChecked);

    private static readonly Lazy<ScanResult> Scan = new(RunScan, LazyThreadSafetyMode.ExecutionAndPublication);

    private static WheelState[] Mechanisms(BeamState b, WheelFamily family) => family switch
    {
        WheelFamily.Color => b.ColorWheels,
        WheelFamily.Gobo => b.Gobos,
        WheelFamily.Prism => b.Prisms,
        WheelFamily.Animation or WheelFamily.AnimationSystem => b.AnimationWheels,
        WheelFamily.Effects => b.Effects,
        WheelFamily.BeamShaper => b.BeamShapers,
        _ => Array.Empty<WheelState>(),
    };

    private static bool Inserted(in WheelState w) => (w.Insertion > 0f && !w.IsOpen) || w.Facets > 0;

    /// <summary>One pass over the library: every mode at its GDTF defaults, and every out-named wheel range.</summary>
    private static ScanResult RunScan()
    {
        var defaults = new ConcurrentDictionary<string, string>();
        var outFailures = new ConcurrentQueue<string>();
        var slotItems = new ConcurrentQueue<string>();
        var lost = new ConcurrentQueue<string>();
        int outChecked = 0, files = 0, selectionsChecked = 0;
        var families = new[] { WheelFamily.Gobo, WheelFamily.Prism, WheelFamily.Animation, WheelFamily.BeamShaper };
        Parallel.ForEach(TestData.Library.Entries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, entry =>
        {
            FixtureType ft;
            try
            {
                ft = GdtfReader.ReadFile(entry.FilePath);
            }
            catch (Exception)
            {
                return; // unreadable files are reported by LibraryWideTests
            }
            Interlocked.Increment(ref files);
            foreach (var mode in ft.DmxModes)
            {
                FixtureProfile p;
                try
                {
                    p = FixtureProfile.Compile(ft, mode.Name, entry.Specs);
                }
                catch (Exception)
                {
                    continue;
                }
                var buffer = new FixtureDmxBuffer(p);
                var state = FixtureEvaluator.Evaluate(p, buffer);
                foreach (var b in state.Beams)
                    foreach (var family in families)
                        foreach (var w in Mechanisms(b, family))
                            if (Inserted(w))
                                defaults.TryAdd(entry.FileName, $"[{mode.Name}] {family} wheel {w.Wheel} slot {w.Slot} insertion {w.Insertion:0.##} facets {w.Facets}");

                foreach (var f in p.Functions)
                {
                    if (f.Class.Wheel == WheelFamily.None) continue;
                    if (f.Class.Kind is not (AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake)) continue;
                    var ch = p.Channels[f.Channel];
                    if (ch.Beams.Count == 0) continue;
                    foreach (var set in f.Sets)
                    {
                        // "Open/Red", "Open+Frost" are split or combined positions, not out ranges
                        if (!OutName().IsMatch(set.Name) || NameParsing.IsInsertionRampName(set.Name)) continue;
                        if (set.Name.AsSpan().IndexOfAny('/', '+') >= 0 && !NameParsing.IsPlainOpenSlotName(set.Name)) continue;
                        buffer.ResetToDefaults();
                        buffer.SetValue(ch.Index, set.DmxFrom);
                        if (f.ModeMaster >= 0) buffer.SetValue(f.ModeMaster, f.ModeFrom);
                        FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: false);
                        if (state.GetChannel(ch.Index).Function != f.Index) continue;
                        Interlocked.Increment(ref outChecked);
                        int n = Math.Max(f.Class.Number, 1) - 1;
                        foreach (int bi in ch.Beams)
                        {
                            var mechanisms = Mechanisms(state.Beams[bi], f.Class.Wheel);
                            if (mechanisms.Length == 0) continue;
                            var w = mechanisms[Math.Min(n, mechanisms.Length - 1)];
                            if (!Inserted(w)) continue;
                            // the range selects a slot that holds something: the slot decides (ACME Sana's prism "Close" and
                            // "Open" select the hole and the prism; ACME Ares "Open 2" … are reducer apertures)
                            var slot = set.WheelSlot > 0 && f.Wheel >= 0 ? p.Wheels[f.Wheel].Source.GetSlot(set.WheelSlot) : null;
                            if (slot is not null && IsPhysicalItem(slot))
                                slotItems.Enqueue($"{entry.FileName} {f.Attribute} '{set.Name}' → slot '{slot.Name}'");
                            else
                                outFailures.Enqueue($"{entry.FileName} [{mode.Name}] {f.Attribute} '{f.Name}' set '{set.Name}' (DMX {set.DmxFrom}): " +
                                    $"wheel {w.Wheel} slot {w.Slot} insertion {w.Insertion:0.##} facets {w.Facets}");
                            break;
                        }
                    }
                }

                // every range that selects a slot holding an item shows it, whatever the other channels' defaults say
                foreach (var f in p.Functions)
                {
                    if (f.Wheel < 0 || f.SecondarySelection || f.Class.Kind is not (AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin
                            or AttributeKind.WheelSelectShake))
                        continue;
                    var ch = p.Channels[f.Channel];
                    if (ch.Beams.Count == 0) continue;
                    var wheel = p.Wheels[f.Wheel];
                    foreach (var set in f.Sets)
                    {
                        if (set.WheelSlot <= 0 || set.WheelSlot > wheel.SlotCount || wheel.SlotOpen[set.WheelSlot - 1]) continue;
                        if ((set.Flags & (ChannelSetFlags.WheelOut | ChannelSetFlags.InsertionRamp | ChannelSetFlags.InsertionCycle)) != 0) continue;
                        buffer.ResetToDefaults();
                        buffer.SetValue(ch.Index, set.DmxFrom + (set.DmxTo - set.DmxFrom) / 2);
                        if (f.ModeMaster >= 0) buffer.SetValue(f.ModeMaster, f.ModeFrom);
                        FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: false);
                        if (state.GetChannel(ch.Index).Function != f.Index) continue;
                        var mechanisms = Mechanisms(state.Beams[ch.Beams[0]], f.Class.Wheel);
                        if (mechanisms.Length == 0) continue;
                        Interlocked.Increment(ref selectionsChecked);
                        var w = mechanisms[Math.Min(Math.Max(f.Class.Number, 1) - 1, mechanisms.Length - 1)];
                        bool shown = (f.Class.Wheel == WheelFamily.Color ? !w.IsOpen : Inserted(w)) && w.Wheel == f.Wheel &&
                            (w.Slot == set.WheelSlot || (w.Blend > 0f && w.NextSlot == set.WheelSlot));
                        if (!shown)
                            lost.Enqueue($"{entry.FileName} [{mode.Name}] {f.Attribute} '{f.Name}' set '{set.Name}' → slot {set.WheelSlot}: " +
                                $"wheel {w.Wheel} slot {w.Slot} open {w.IsOpen} insertion {w.Insertion:0.##}");
                    }
                }
            }
        });
        return new ScanResult(defaults, outFailures.ToList(), outChecked, slotItems.ToList(), files, lost.ToList(), selectionsChecked);
    }

    /// <summary>A slot that holds something by the file's own description: an image, prism facets, an animation system or a colour filter.</summary>
    private static bool IsPhysicalItem(WheelSlot slot) =>
        slot.MediaFileName is not null || slot.Facets.Count > 0 || slot.AnimationSystem is not null ||
        MathF.Abs(slot.Color.X - WheelSlot.DefaultColor.X) > 0.005f || MathF.Abs(slot.Color.Y - WheelSlot.DefaultColor.Y) > 0.005f;

    [Fact]
    public void Across_the_library_gdtf_defaults_leave_gobos_prisms_animations_and_shapers_out()
    {
        var scan = Scan.Value;
        Assert.Equal(TestData.Library.Count, scan.Files);
        foreach (var (file, what) in scan.InsertedAtDefaults.OrderBy(k => k.Key, StringComparer.Ordinal)) output.WriteLine($"{file}: {what}");
        var unexpected = scan.InsertedAtDefaults.Keys
            .Where(file => !InsertedAtDefaults.Keys.Any(prefix => file.StartsWith(prefix + "@", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToList();
        Assert.True(unexpected.Count == 0, "inserted at GDTF defaults: " + string.Join("; ", unexpected.Select(f => $"{f}: {scan.InsertedAtDefaults[f]}")));
        // every allowed entry is still a real case
        foreach (var prefix in InsertedAtDefaults.Keys)
            Assert.True(scan.InsertedAtDefaults.Keys.Any(f => f.StartsWith(prefix + "@", StringComparison.Ordinal)), $"{prefix} no longer inserts anything at defaults");
    }

    [Fact]
    public void Across_the_library_ranges_named_open_removed_close_or_off_take_the_mechanism_out()
    {
        var scan = Scan.Value;
        output.WriteLine($"{scan.OutSetsChecked} out-named ranges evaluated; {scan.SlotItems.Count} select a slot holding an item");
        foreach (var s in scan.SlotItems.Take(30)) output.WriteLine("  " + s);
        foreach (var f in scan.OutFailures.Take(30)) output.WriteLine("FAIL " + f);
        Assert.True(scan.OutSetsChecked > 5000, $"only {scan.OutSetsChecked} ranges checked");
        Assert.Empty(scan.OutFailures);
    }

    [Fact]
    public void Across_the_library_selecting_a_slot_that_holds_an_item_shows_it_whatever_the_other_channels_say()
    {
        // channels the file gives the same mechanism — a rotation channel with "Prism Rotation Off" written as Prism1, a
        // second prism channel with the first prism's attribute at "No Effect", an item index written as a wheel index at
        // DMX 0 — must not take a selected gobo, prism or colour out of the beam or move the wheel off it
        var scan = Scan.Value;
        output.WriteLine($"{scan.SelectionsChecked} item selections evaluated");
        var unexpected = scan.LostSelections
            .Where(l => !SelectionOverridden.Keys.Any(prefix => l.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();
        foreach (var l in unexpected.Take(30)) output.WriteLine("LOST " + l);
        Assert.True(scan.SelectionsChecked > 20000, $"only {scan.SelectionsChecked} selections checked");
        Assert.Empty(unexpected);
        foreach (var prefix in SelectionOverridden.Keys)
            Assert.True(scan.LostSelections.Any(l => l.StartsWith(prefix, StringComparison.Ordinal)), $"{prefix} no longer loses a selection");
    }

    [Fact]
    public void A_rotation_channel_written_with_the_prism_attribute_neither_takes_the_prism_out_nor_puts_it_in()
    {
        // ADJ Focus Hybrid: the "Prism1Pos" channel starts with "Off" / "Prism Rotation Off" (0 … 5) and has "No Rotation"
        // at 192, both written as Prism1; the prism channel selects the 8-facet prism from 32
        var p = TestData.Profile("ADJ@Focus_Hybrid", "Basic Mode 19ch Pan540");
        var buffer = OpenWhite(p);
        int prism = Channel(p, "Prism1"), pos = p.Channels.First(c => c.Name.EndsWith("_Prism1Pos", StringComparison.Ordinal)).Index;
        buffer.SetValue(prism, 100);
        buffer.SetValue(pos, 0);
        var s = Evaluate(p, buffer);
        Assert.Equal(8, s.Beams[0].Prisms[0].Facets);
        buffer.SetValue(pos, 150); // rotating
        s = Evaluate(p, buffer);
        Assert.Equal(8, s.Beams[0].Prisms[0].Facets);

        buffer.SetValue(prism, 0); // "No Effect"
        foreach (uint v in new uint[] { 0, 100, 150, 192 })
        {
            buffer.SetValue(pos, v);
            s = Evaluate(p, buffer);
            Assert.Equal(0, s.Beams[0].PrismFacets);
        }
    }

    [Theory]
    [InlineData("ADJ@Protege_XS", "Basic", "Body_Prism1", "Body_Prism2")]
    [InlineData("ADJ@Vizi_Beam_12RX", "13 CH", "Head _Prism1", "Head _Prism2")]
    public void A_second_prism_channel_at_its_default_does_not_cancel_the_first(string prefix, string mode, string first, string second)
    {
        // both channels are written as Prism1 (one selects "Prism 1", the other "Prism 2"); each starts with an out range
        var p = TestData.Profile(prefix, mode);
        int a = p.Channels.First(c => c.Name == first).Index, b = p.Channels.First(c => c.Name == second).Index;
        var buffer = OpenWhite(p);
        Assert.Equal(0, Evaluate(p, buffer).Beams[0].PrismFacets);
        buffer.SetValue(a, 100);
        Assert.True(Evaluate(p, buffer).Beams[0].PrismFacets > 1, "first prism channel");
        buffer.SetValue(a, 0);
        buffer.SetValue(b, 100);
        Assert.True(Evaluate(p, buffer).Beams[0].PrismFacets > 1, "second prism channel");
    }

    [Fact]
    public void An_animation_macro_channel_at_no_animation_leaves_an_indexed_animation_wheel_in()
    {
        // Robe Robin T1 Profile FS: "AnimationWheel1SelectEffects" defaults to "No animation" (0 … 7) next to the animation
        // wheel channel, whose "Proportional indexing" ranges put the wheel in
        var p = TestData.Profile("Robe_Lighting@Robin_T1_Profile_FS", "Mode 1 - RGB");
        var buffer = OpenWhite(p);
        int anim = p.Channels.First(c => c.Name == "Head_AnimationWheel1").Index;
        Assert.False(Inserted(Evaluate(p, buffer).Beams[0].AnimationWheels[0]));
        buffer.SetValue(anim, 73); // "Proportional indexing center"
        var w = Evaluate(p, buffer).Beams[0].AnimationWheels[0];
        Assert.True(Inserted(w), $"slot {w.Slot} insertion {w.Insertion}");
    }

    [Theory]
    [InlineData("LIGHT_SKY@F420BSW", "24CH", "Head_Gobo2", "Head_Gobo2Pos", 10u)]
    [InlineData("Flash@EVENT_WASH_FX_300", "SH1px - 92ch - shapes + pixel engine RGB", "Shape_Gobo1", "Shape_Gobo1Pos", 8u)]
    public void An_item_index_written_as_a_wheel_index_turns_the_selected_gobo(string prefix, string mode, string select, string index, uint item)
    {
        // the index channel ("Gobo 2 Index", "Shape index") is written as Gobo(n)WheelIndex: at its default it would put the
        // wheel back on its first slot (the open one) and lose the selection
        var p = TestData.Profile(prefix, mode);
        int sel = p.Channels.First(c => c.Name == select).Index, idx = p.Channels.First(c => c.Name == index).Index;
        var buffer = OpenWhite(p);
        buffer.SetValue(sel, item);
        var s = Evaluate(p, buffer);
        int n = Math.Max(p.Functions[s.GetChannel(sel).Function].Class.Number, 1) - 1;
        var w = s.Beams[p.Channels[sel].Beams[0]].Gobos[n];
        Assert.True(Inserted(w), $"slot {w.Slot} open {w.IsOpen}");
        int slot = w.Slot;
        float rotation0 = w.Rotation;

        Assert.True(DmxEncoder.Encode(buffer, p.Functions[p.Channels[idx].FirstFunction].Attribute, 90f) >= 0);
        buffer.SetValue(idx, p.Channels[idx].MaxValue / 8);
        s = Evaluate(p, buffer);
        w = s.Beams[p.Channels[sel].Beams[0]].Gobos[n];
        Assert.Equal(slot, w.Slot);
        Assert.True(Inserted(w));
        Assert.NotEqual(rotation0, w.Rotation);
    }

    [Theory]
    [InlineData("Infinity@Furion_B401_Beam", "Prism1")]
    [InlineData("Vari-Lite@VL2600_Spot", "Prism1")]
    public void A_prism_slot_named_as_a_prism_is_a_prism_without_facet_data(string prefix, string attribute)
    {
        // Infinity Furion B401: the only slot of each prism wheel is "Prism" (no facets, GDTF's default colour), and the
        // wheel is first named by a "NO FUNCTION" function; Vari-Lite VL2600: "Prism On"
        var p = TestData.Profile(prefix);
        var f = p.Functions.First(fn => fn.Attribute == attribute && fn.Wheel >= 0 && fn.Sets.Any(st => st.WheelSlot > 0 && !p.Wheels[fn.Wheel].SlotOpen[st.WheelSlot - 1]));
        var set = f.Sets.First(st => st.WheelSlot > 0 && !p.Wheels[f.Wheel].SlotOpen[st.WheelSlot - 1]);
        Assert.Contains("rism", p.Wheels[f.Wheel].Source.GetSlot(set.WheelSlot)!.Name, StringComparison.Ordinal);
        var buffer = OpenWhite(p);
        Assert.Equal(0, Evaluate(p, buffer).Beams[0].PrismFacets);
        buffer.SetValue(f.Channel, set.DmxFrom + (set.DmxTo - set.DmxFrom) / 2);
        Assert.True(Evaluate(p, buffer).Beams[0].PrismFacets >= 3);
    }

    [Fact]
    public void A_wheel_shake_with_a_slot_per_range_shakes_that_gobo()
    {
        // ADJ Focus Hybrid "Gobo1WheelShake": 'Gobo Shake 1' … 'Gobo Shake 8', each range naming its slot; read as a
        // shake of the whole wheel it stayed on the open slot
        var p = TestData.Profile("ADJ@Focus_Hybrid", "Basic Mode 19ch Pan540");
        var buffer = OpenWhite(p);
        int ch = Channel(p, "Gobo1");
        var f = p.Functions.Single(fn => fn.Channel == ch && fn.Attribute == "Gobo1WheelShake");
        Assert.Equal(AttributeKind.WheelSelectShake, f.Class.Kind);
        var set = f.Sets.First(st => st.Name == "Gobo Shake 1");
        buffer.SetValue(ch, set.DmxFrom + 2);
        var s = Evaluate(p, buffer);
        var w = s.Beams[0].Gobos[0];
        Assert.True(Inserted(w), $"slot {w.Slot} open {w.IsOpen}");
        Assert.Equal(set.WheelSlot, w.Slot);
        Assert.True(s.IsAnimating);
        Assert.True(w.ShakeFrequency > 0f);
    }

    private static FixtureState Evaluate(FixtureProfile p, FixtureDmxBuffer buffer, double time = 0) => FixtureEvaluator.Evaluate(p, buffer, time);

    private static FixtureDmxBuffer OpenWhite(FixtureProfile p)
    {
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        return buffer;
    }

    private static int Channel(FixtureProfile p, string attribute)
    {
        var channels = p.FindChannels(attribute);
        Assert.True(channels.Count > 0, $"{p} has no {attribute} channel");
        return channels[0];
    }

    private static void AssertNothingInserted(FixtureState s, string what)
    {
        foreach (var b in s.Beams)
            foreach (var family in new[] { WheelFamily.Gobo, WheelFamily.Prism, WheelFamily.Animation, WheelFamily.BeamShaper })
                foreach (var w in Mechanisms(b, family))
                    Assert.False(Inserted(w), $"{what}: {b.Name} {family} wheel {w.Wheel} slot {w.Slot} insertion {w.Insertion} facets {w.Facets}");
    }

    [Theory]
    [InlineData(Rivale, "Standard_540", "AnimationWheel1", 8u)]
    [InlineData(Chiron, "Mode 2 17 DMX", "Prism1", 8u)]
    [InlineData(Chiron, "Mode 1 14 DMX", "Prism1", 8u)]
    [InlineData(Picasso, "Standard Pan540", "AnimationWheel1", 8u)]
    [InlineData(Mondrian, "Extended Pan540", "AnimationWheel1", 8u)]
    public void Reported_fixtures_are_clear_at_defaults_and_open_white_and_insert_when_selected(string prefix, string mode, string attribute, uint inDmx)
    {
        var p = TestData.Profile(prefix, mode);
        var defaults = new FixtureDmxBuffer(p);
        AssertNothingInserted(Evaluate(p, defaults), $"{prefix} [{mode}] defaults");
        var white = OpenWhite(p);
        AssertNothingInserted(Evaluate(p, white), $"{prefix} [{mode}] open white");

        // the first range after the out range puts the mechanism in
        white.SetValue(Channel(p, attribute), inDmx);
        var s = Evaluate(p, white);
        var family = attribute.StartsWith("Prism", StringComparison.Ordinal) ? WheelFamily.Prism : WheelFamily.Animation;
        var w = Mechanisms(s.Beams[0], family)[0];
        Assert.True(Inserted(w), $"{prefix} {attribute} at {inDmx}: slot {w.Slot} insertion {w.Insertion} facets {w.Facets}");
        if (family == WheelFamily.Prism) Assert.Equal(8, w.Facets);
    }

    [Fact]
    public void Indexing_a_prism_does_not_insert_it()
    {
        // ADJ Focus Profile: Prism1 'No Effect' 0 … 31; Prism1Pos only turns the prism, it does not bring it in
        var p = TestData.Profile(FocusProfile, "Extended Mode 51ch");
        var buffer = OpenWhite(p);
        buffer.SetValue(Channel(p, "Prism1"), 0);
        Assert.True(DmxEncoder.Encode(buffer, "Prism1Pos", 90f) > 0);
        var s = Evaluate(p, buffer);
        Assert.Equal(0, s.Beams[0].Prisms[0].Facets);
        buffer.SetValue(Channel(p, "Prism1"), 32);
        s = Evaluate(p, buffer);
        Assert.Equal(3, s.Beams[0].Prisms[0].Facets);
    }

    [Fact]
    public void An_open_range_selecting_the_empty_slot_of_an_animation_wheel_leaves_it_out()
    {
        // PRstage Spot 460: 'Open' selects 'Wheel Slot 1' (no media), 'Index' selects the animation disc
        var p = TestData.Profile(PrSpot);
        var buffer = OpenWhite(p);
        int ch = Channel(p, "AnimationWheel1");
        buffer.SetValue(ch, 0);
        var anim = Evaluate(p, buffer).Beams[0].AnimationWheels[0];
        Assert.False(Inserted(anim), $"slot {anim.Slot} insertion {anim.Insertion}");
        buffer.SetValue(ch, 1);
        anim = Evaluate(p, buffer).Beams[0].AnimationWheels[0];
        Assert.True(Inserted(anim));
        Assert.Equal(2, anim.Slot);
    }

    [Fact]
    public void A_ramp_from_open_to_animation_in_inserts_the_wheel_partially()
    {
        // Cameo Oron H2: 'Open' 0, an unnamed range 1 … 254, 'Animation In' 255
        var p = TestData.Profile(OronH2, "33CH");
        var buffer = OpenWhite(p);
        int ch = Channel(p, "AnimationWheel1");
        float Insertion(uint dmx)
        {
            buffer.SetValue(ch, dmx);
            var w = Evaluate(p, buffer).Beams[0].AnimationWheels[0];
            return w.IsOpen ? 0f : w.Insertion;
        }

        Assert.Equal(0f, Insertion(0));
        float previous = 0f;
        foreach (uint dmx in new uint[] { 32, 96, 160, 224 })
        {
            float ins = Insertion(dmx);
            Assert.InRange(ins, 0.01f, 0.99f);
            Assert.True(ins > previous, $"insertion {ins} at {dmx} after {previous}");
            previous = ins;
        }
        Assert.Equal(1f, Insertion(255), 1e-3f);
    }

    [Fact]
    public void Ramp_cycles_move_the_insertion_between_their_named_ends()
    {
        // Robe Esprite: 'No function' 0 … 19, 'Effect wheel indexing' 20 … 127, then ramp cycles
        // open→full 128 … 170, open→half 171 … 213, half→full 214 … 255
        var p = TestData.Profile(Esprite, "Mode 2 - Reduced 8 bit");
        var buffer = OpenWhite(p);
        int ch = Channel(p, "AnimationWheel1");
        buffer.SetValue(ch, 0);
        Assert.False(Inserted(Evaluate(p, buffer).Beams[0].AnimationWheels[0]));
        buffer.SetValue(ch, 60);
        var indexed = Evaluate(p, buffer).Beams[0].AnimationWheels[0];
        Assert.True(Inserted(indexed));
        Assert.Equal(1f, indexed.Insertion);

        foreach (var (dmx, lo, hi) in new[] { (150u, 0f, 1f), (190u, 0f, 0.5f), (235u, 0.5f, 1f) })
        {
            buffer.SetValue(ch, dmx);
            float min = float.MaxValue, max = float.MinValue;
            bool animating = true;
            for (double t = 0; t <= 41; t += 0.2)
            {
                var s = Evaluate(p, buffer, t);
                var w = s.Beams[0].AnimationWheels[0];
                float ins = w.IsOpen ? 0f : w.Insertion;
                min = MathF.Min(min, ins);
                max = MathF.Max(max, ins);
                animating &= s.IsAnimating;
            }
            Assert.True(animating, $"DMX {dmx} does not animate");
            Assert.InRange(min, lo - 1e-3f, lo + 0.05f);
            Assert.InRange(max, hi - 0.05f, hi + 1e-3f);
        }
    }

    [Fact]
    public void Rivale_beam_shaper_selects_its_lens_and_stretches_the_beam()
    {
        var p = TestData.Profile(Rivale, "Standard_540");
        var buffer = OpenWhite(p);
        int ch = Channel(p, "BeamShaper");
        buffer.SetValue(ch, 0);
        var open = Evaluate(p, buffer).Beams[0];
        Assert.False(Inserted(open.BeamShapers[0]));
        Assert.Equal(1f, open.ShaperStretch);

        buffer.SetValue(ch, 128);
        var s = Evaluate(p, buffer);
        Assert.Equal(2f, s.GetChannel(ch).Value, 1e-3f); // the second of two slots, not wrapped back to the first
        var shaped = s.Beams[0];
        var w = shaped.BeamShapers[0];
        Assert.Equal(2, w.Slot);
        Assert.False(w.IsOpen);
        Assert.Equal(1f, w.Insertion);
        Assert.Equal(NameParsing.DefaultShaperStretch, shaped.ShaperStretch, 1e-4f);
        // the same flux spread over an elongated beam: the peak on the axis drops by the stretch
        Assert.Equal(open.Flux, shaped.Flux, open.Flux * 1e-3f);
        Assert.Equal(open.PeakCandela / shaped.ShaperStretch, shaped.PeakCandela, open.PeakCandela * 0.01f);

        // the long axis follows the lens index (8-bit: compare with the decoded index angles)
        int pos = Channel(p, "BeamShaperPos");
        Assert.True(DmxEncoder.Encode(buffer, "BeamShaperPos", 0f) > 0);
        var s0 = Evaluate(p, buffer);
        Assert.True(DmxEncoder.Encode(buffer, "BeamShaperPos", 45f) > 0);
        var s45 = Evaluate(p, buffer);
        float expected = (s45.GetChannel(pos).Value - s0.GetChannel(pos).Value) * MathF.PI / 180f;
        Assert.InRange(expected, 40f * MathF.PI / 180f, 50f * MathF.PI / 180f);
        float delta = MathF.IEEERemainder(s45.Beams[0].ShaperStretchRotation - s0.Beams[0].ShaperStretchRotation, 2f * MathF.PI);
        Assert.Equal(expected, delta, 1e-3f);
    }

    [Fact]
    public void Field_angles_derived_from_a_zoom_range_written_as_beam_and_field_are_marked_estimated()
    {
        // the Rivale Wash writes its zoom range (4° … 57°) as BeamAngle/FieldAngle: the field angle is the beam type's
        // typical ratio, so at a wide zoom it exceeds the file's 57°, and says so
        var rivale = TestData.Profile(Rivale, "Standard_540");
        Assert.True(rivale.Beams[0].FieldRatioEstimated);
        Assert.Contains(rivale.Warnings, w => w.Contains("zoom range", StringComparison.Ordinal));
        var wide = Evaluate(rivale, OpenWhiteAtZoom(rivale, 1f)).Beams[0];
        Assert.True(wide.FieldAngleEstimated);
        Assert.True(wide.FieldAngle > wide.BeamAngle);

        // a file with a real beam/field pair keeps its ratio and is not an estimate
        var viper = TestData.Profile("Martin_Professional@MAC_Viper_Profile", "Basic");
        Assert.False(viper.Beams[0].FieldRatioEstimated);
        var b = Evaluate(viper, OpenWhiteAtZoom(viper, 0.5f)).Beams[0];
        Assert.False(b.FieldAngleEstimated);
        Assert.Equal(21.875f / 20f, b.FieldAngle / b.BeamAngle, 0.01f);
    }

    private static FixtureDmxBuffer OpenWhiteAtZoom(FixtureProfile p, float zoom)
    {
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer, zoom);
        return buffer;
    }
}
