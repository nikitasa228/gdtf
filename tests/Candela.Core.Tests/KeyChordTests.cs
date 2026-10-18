using Candela.Core.Settings;

namespace Candela.Core.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+S", "S", KeyModifiers.Ctrl | KeyModifiers.Shift, "Ctrl+Shift+S")]
    [InlineData("ctrl+s", "s", KeyModifiers.Ctrl, "Ctrl+s")]
    [InlineData(" Shift + Ctrl + Z ", "Z", KeyModifiers.Ctrl | KeyModifiers.Shift, "Ctrl+Shift+Z")]
    [InlineData("A+Ctrl", "A", KeyModifiers.Ctrl, "Ctrl+A")]
    [InlineData("Control+Alt+Delete", "Delete", KeyModifiers.Ctrl | KeyModifiers.Alt, "Ctrl+Alt+Delete")]
    [InlineData("Cmd+Comma", "Comma", KeyModifiers.Super, "Super+Comma")]
    [InlineData("Win+E", "E", KeyModifiers.Super, "Super+E")]
    [InlineData("Option+1", "1", KeyModifiers.Alt, "Alt+1")]
    [InlineData("Numpad7", "Numpad7", KeyModifiers.None, "Numpad7")]
    [InlineData("F12", "F12", KeyModifiers.None, "F12")]
    [InlineData("Ctrl+Shift+Alt+Super+PageDown", "PageDown", KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Super,
        "Ctrl+Shift+Alt+Super+PageDown")]
    [InlineData("Alt+Hyper", "Hyper", KeyModifiers.Alt, "Alt+Hyper")] // unknown key names are kept: the windowing layer decides
    public void Valid_chords_parse(string text, string key, KeyModifiers modifiers, string canonical)
    {
        var chord = KeyChord.Parse(text);
        Assert.Equal(key, chord.Key);
        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(canonical, chord.ToString());
        Assert.True(KeyChord.TryParse(text, out var again));
        Assert.Equal(chord, again);
        Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
    }

    [Theory]
    [InlineData("Ctrl+A+B", "more than one key")]
    [InlineData("Shift+F1+F2", "more than one key")]
    [InlineData("Hyper+A", "unknown modifier 'Hyper'")]
    [InlineData("A+Hyper", "unknown modifier 'Hyper'")]
    [InlineData("Ctrl+Mega+Hyper+S", "unknown modifier 'Mega'")]
    [InlineData("Foo+Bar", "unknown modifier 'Foo'")]
    [InlineData("Ctrl+Ctrl+A", "twice")]
    [InlineData("Ctrl+Control+A", "twice")]
    [InlineData("Ctrl+Shift", "has no key")]
    [InlineData("Shift", "has no key")]
    [InlineData("Ctrl+", "empty part")]
    [InlineData("Ctrl++A", "empty part")]
    [InlineData("+A", "empty part")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    public void Invalid_chords_are_rejected_with_a_clear_message(string text, string reason)
    {
        var e = Assert.Throws<FormatException>(() => KeyChord.Parse(text));
        Assert.Contains(reason, e.Message, StringComparison.Ordinal);
        Assert.False(KeyChord.TryParse(text, out var chord));
        Assert.Equal(default, chord);
        Assert.False(KeyChord.TryParse(text, out _, out var error));
        Assert.Equal(e.Message, error);
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => KeyChord.Parse(null!));
        Assert.False(KeyChord.TryParse(null, out _));
        Assert.False(KeyChord.TryParse(null, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Every_default_binding_parses_to_the_chord_it_was_made_from()
    {
        // The chords CreateDefault binds, written out independently of their text form.
        var c = KeyModifiers.Ctrl;
        var s = KeyModifiers.Shift;
        var a = KeyModifiers.Alt;
        var expected = new Dictionary<string, KeyChord[]>
        {
            [EditorActions.Undo] = new[] { new KeyChord("Z", c) },
            [EditorActions.Redo] = new[] { new KeyChord("Y", c), new KeyChord("Z", c | s) },
            [EditorActions.Delete] = new[] { new KeyChord("Delete") },
            [EditorActions.Duplicate] = new[] { new KeyChord("D", c) },
            [EditorActions.Copy] = new[] { new KeyChord("C", c) },
            [EditorActions.Paste] = new[] { new KeyChord("V", c) },
            [EditorActions.SelectAll] = new[] { new KeyChord("A", c) },
            [EditorActions.DeselectAll] = new[] { new KeyChord("Escape") },
            [EditorActions.ToolSelect] = new[] { new KeyChord("Q") },
            [EditorActions.ToolMove] = new[] { new KeyChord("W") },
            [EditorActions.ToolRotate] = new[] { new KeyChord("E") },
            [EditorActions.ToolCable] = new[] { new KeyChord("C") },
            [EditorActions.ToolWall] = new[] { new KeyChord("B") },
            [EditorActions.FrameSelection] = new[] { new KeyChord("F") },
            [EditorActions.ViewTop] = new[] { new KeyChord("Numpad7") },
            [EditorActions.ViewFront] = new[] { new KeyChord("Numpad1") },
            [EditorActions.ViewRight] = new[] { new KeyChord("Numpad3") },
            [EditorActions.ViewToggleOrtho] = new[] { new KeyChord("Numpad5") },
            [EditorActions.ShadingWireframe] = new[] { new KeyChord("1", a) },
            [EditorActions.ShadingColorless] = new[] { new KeyChord("2", a) },
            [EditorActions.ShadingFull] = new[] { new KeyChord("3", a) },
            [EditorActions.OverlayLux] = new[] { new KeyChord("L", a) },
            [EditorActions.OverlayNoise] = new[] { new KeyChord("N", a) },
            [EditorActions.OverlayCables] = new[] { new KeyChord("C", a) },
            [EditorActions.FileNew] = new[] { new KeyChord("N", c) },
            [EditorActions.FileOpen] = new[] { new KeyChord("O", c) },
            [EditorActions.FileSave] = new[] { new KeyChord("S", c) },
            [EditorActions.FileSaveAs] = new[] { new KeyChord("S", c | s) },
            [EditorActions.FileImport] = new[] { new KeyChord("I", c) },
            [EditorActions.Render] = new[] { new KeyChord("F12") },
            [EditorActions.Settings] = new[] { new KeyChord("Comma", c) },
        };

        var set = KeyBindingSet.CreateDefault();
        Assert.Equal(expected.Keys.Order(), set.Bindings.Keys.Order());
        foreach (var (action, chords) in expected)
        {
            Assert.Equal(chords, set.ChordsFor(action));
            Assert.Equal(chords.Select(ch => ch.ToString()), set.Bindings[action]);
            foreach (var text in set.Bindings[action])
            {
                Assert.True(KeyChord.IsKnownKey(KeyChord.Parse(text).Key), text);
                Assert.Contains(action, set.ActionsFor(KeyChord.Parse(text)));
            }
        }
        Assert.Empty(set.FindInvalidChords());
    }

    [Fact]
    public void A_hand_edited_preset_with_bad_chords_still_works()
    {
        var set = new KeyBindingSet
        {
            Bindings =
            {
                ["edit.undo"] = new List<string> { "Ctrl+Z", "Ctrl+Z+Y", "Hyper+Z" },
                ["edit.redo"] = new List<string> { "Ctrl+Y", null! },
                ["edit.copy"] = null!,
            },
        };

        Assert.Equal(new[] { new KeyChord("Z", KeyModifiers.Ctrl) }, set.ChordsFor("edit.undo"));
        Assert.Empty(set.ChordsFor("edit.copy"));
        Assert.Empty(set.ChordsFor("missing"));
        Assert.Equal(new[] { "edit.redo" }, set.ActionsFor(KeyChord.Parse("Ctrl+Y")));

        var invalid = set.FindInvalidChords().ToList();
        Assert.Equal(3, invalid.Count);
        Assert.Contains(invalid, i => i.Text == "Ctrl+Z+Y" && i.Error.Contains("more than one key", StringComparison.Ordinal));
        Assert.Contains(invalid, i => i.Text == "Hyper+Z" && i.Error.Contains("unknown modifier", StringComparison.Ordinal));
        Assert.Contains(invalid, i => i.Action == "edit.redo" && i.Text is null);
    }

    [Fact]
    public void Known_keys_cover_the_documented_names()
    {
        foreach (var key in new[] { "A", "z", "0", "9", "F1", "F24", "Delete", "Escape", "Enter", "Space", "Tab", "Backspace", "Up", "Down",
                     "Left", "Right", "Home", "End", "PageUp", "PageDown", "Numpad0", "Numpad9", "Minus", "Equal", "Comma", "Period" })
        {
            Assert.True(KeyChord.IsKnownKey(key), key);
        }
        Assert.False(KeyChord.IsKnownKey("Ctrl"));
        Assert.False(KeyChord.IsKnownKey("Hyper"));
        Assert.False(KeyChord.IsKnownKey(null));
    }
}
