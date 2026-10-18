using Candela.Core.Settings;
using Hexa.NET.ImGui;

namespace Candela.App.Input;

/// <summary>
/// Maps the windowing-independent key names of Core's <see cref="KeyChord"/> ("A", "F5", "Delete", "Numpad7", "Comma"…)
/// to Dear ImGui keys and back. Unknown names map to <see cref="ImGuiKey.None"/>.
/// </summary>
internal static class KeyNames
{
    private static readonly Dictionary<string, ImGuiKey> ByName = Build();
    private static readonly Dictionary<ImGuiKey, string> ByKey = ByName.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    /// <summary>Keys that can be bound (letters, digits, function keys, navigation, numpad, punctuation).</summary>
    public static IReadOnlyCollection<ImGuiKey> BindableKeys => ByKey.Keys;

    /// <summary>The ImGui key of a Core key name (case-insensitive), or <see cref="ImGuiKey.None"/>.</summary>
    public static ImGuiKey ToImGui(string? name) =>
        name is not null && ByName.TryGetValue(name, out var key) ? key : ImGuiKey.None;

    /// <summary>The Core key name of an ImGui key, or null when it cannot be bound (modifiers, mouse, gamepad).</summary>
    public static string? FromImGui(ImGuiKey key) => ByKey.GetValueOrDefault(key);

    /// <summary>True for modifier keys (they only combine with other keys).</summary>
    public static bool IsModifier(ImGuiKey key) => key is ImGuiKey.LeftCtrl or ImGuiKey.RightCtrl or ImGuiKey.LeftShift or ImGuiKey.RightShift
        or ImGuiKey.LeftAlt or ImGuiKey.RightAlt or ImGuiKey.LeftSuper or ImGuiKey.RightSuper or ImGuiKey.ModCtrl or ImGuiKey.ModShift
        or ImGuiKey.ModAlt or ImGuiKey.ModSuper;

    /// <summary>The ImGui key chord (key | modifier flags) of a Core chord.</summary>
    public static ImGuiKey ToImGuiChord(KeyChord chord)
    {
        var key = ToImGui(chord.Key);
        if (key == ImGuiKey.None)
            return ImGuiKey.None;
        if ((chord.Modifiers & KeyModifiers.Ctrl) != 0)
            key |= ImGuiKey.ModCtrl;
        if ((chord.Modifiers & KeyModifiers.Shift) != 0)
            key |= ImGuiKey.ModShift;
        if ((chord.Modifiers & KeyModifiers.Alt) != 0)
            key |= ImGuiKey.ModAlt;
        if ((chord.Modifiers & KeyModifiers.Super) != 0)
            key |= ImGuiKey.ModSuper;
        return key;
    }

    /// <summary>Core modifiers from ImGui's current modifier state.</summary>
    public static KeyModifiers ModifiersOf(bool ctrl, bool shift, bool alt, bool super)
    {
        var m = KeyModifiers.None;
        if (ctrl)
            m |= KeyModifiers.Ctrl;
        if (shift)
            m |= KeyModifiers.Shift;
        if (alt)
            m |= KeyModifiers.Alt;
        if (super)
            m |= KeyModifiers.Super;
        return m;
    }

    private static Dictionary<string, ImGuiKey> Build()
    {
        var d = new Dictionary<string, ImGuiKey>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 26; i++)
            d[((char)('A' + i)).ToString()] = ImGuiKey.A + i;
        for (int i = 0; i < 10; i++)
        {
            d[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = ImGuiKey.Key0 + i;
            d["Numpad" + i] = ImGuiKey.Keypad0 + i;
        }

        for (int i = 1; i <= 12; i++)
            d["F" + i] = ImGuiKey.F1 + (i - 1);
        d["Delete"] = ImGuiKey.Delete;
        d["Escape"] = ImGuiKey.Escape;
        d["Enter"] = ImGuiKey.Enter;
        d["Space"] = ImGuiKey.Space;
        d["Tab"] = ImGuiKey.Tab;
        d["Backspace"] = ImGuiKey.Backspace;
        d["Insert"] = ImGuiKey.Insert;
        d["Up"] = ImGuiKey.UpArrow;
        d["Down"] = ImGuiKey.DownArrow;
        d["Left"] = ImGuiKey.LeftArrow;
        d["Right"] = ImGuiKey.RightArrow;
        d["Home"] = ImGuiKey.Home;
        d["End"] = ImGuiKey.End;
        d["PageUp"] = ImGuiKey.PageUp;
        d["PageDown"] = ImGuiKey.PageDown;
        d["Minus"] = ImGuiKey.Minus;
        d["Equal"] = ImGuiKey.Equal;
        d["Comma"] = ImGuiKey.Comma;
        d["Period"] = ImGuiKey.Period;
        d["Slash"] = ImGuiKey.Slash;
        d["Semicolon"] = ImGuiKey.Semicolon;
        d["Apostrophe"] = ImGuiKey.Apostrophe;
        d["LeftBracket"] = ImGuiKey.LeftBracket;
        d["RightBracket"] = ImGuiKey.RightBracket;
        d["Backslash"] = ImGuiKey.Backslash;
        d["GraveAccent"] = ImGuiKey.GraveAccent;
        d["NumpadDecimal"] = ImGuiKey.KeypadDecimal;
        d["NumpadDivide"] = ImGuiKey.KeypadDivide;
        d["NumpadMultiply"] = ImGuiKey.KeypadMultiply;
        d["NumpadSubtract"] = ImGuiKey.KeypadSubtract;
        d["NumpadAdd"] = ImGuiKey.KeypadAdd;
        d["NumpadEnter"] = ImGuiKey.KeypadEnter;
        return d;
    }
}
