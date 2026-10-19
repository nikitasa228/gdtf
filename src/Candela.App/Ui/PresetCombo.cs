using System.Numerics;
using System.Text;
using Candela.App.Session;
using Candela.Core.Presets;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// The preset dropdown that sits beside every configurable group (requirement: "Default", saved names, "Custom" as soon
/// as anything is changed, and "Save as…" to keep the custom values under a name): a combo box, a save button with a
/// name prompt and a delete button for saved presets. Works on a <see cref="PresetBinding{T}"/>. Main thread only.
/// </summary>
/// <typeparam name="T">Settings type.</typeparam>
internal sealed unsafe class PresetCombo<T> where T : class
{
    private readonly PresetBinding<T> _binding;
    private readonly string _comboId;
    private readonly string _popupId;
    private readonly byte[] _name = new byte[128];
    private bool _focusName;

    /// <summary>Creates the dropdown for <paramref name="binding"/>; <paramref name="id"/> keeps several dropdowns apart.</summary>
    public PresetCombo(PresetBinding<T> binding, string id)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ArgumentException.ThrowIfNullOrEmpty(id);
        _comboId = "##preset-" + id;
        _popupId = "##preset-save-" + id;
    }

    /// <summary>The binding.</summary>
    public PresetBinding<T> Binding => _binding;

    /// <summary>The text shown for a preset name ("Default" and "Custom" are translated).</summary>
    public static string DisplayName(string name) => name switch
    {
        PresetLibrary<T>.DefaultName => Loc.Get(Str.PresetDefault),
        PresetLibrary<T>.CustomName => Loc.Get(Str.PresetCustom),
        _ => name,
    };

    /// <summary>
    /// Draws the dropdown and its buttons on one line, filling <paramref name="width"/> pixels (−1: the rest of the line).
    /// Returns true when a preset was selected (the live values changed).
    /// </summary>
    public bool Draw(float width = -1f)
    {
        bool changed = false;
        var style = ImGui.GetStyle();
        float button = ImGui.GetFrameHeight();
        float available = width > 0f ? width : ImGui.GetContentRegionAvail().X;
        bool canDelete = !_binding.IsCustom && _binding.CurrentName != PresetLibrary<T>.DefaultName;
        float comboWidth = MathF.Max(60f, available - 2f * (button + style.ItemInnerSpacing.X));

        ImGui.SetNextItemWidth(comboWidth);
        string current = DisplayName(_binding.CurrentName);
        if (ImGui.BeginCombo(_comboId, current))
        {
            if (ImGui.IsWindowAppearing())
                _binding.RefreshNames();
            foreach (string name in _binding.Names)
            {
                bool selected = name == _binding.CurrentName;
                if (name == PresetLibrary<T>.CustomName)
                {
                    // "Custom" is the edited state; it is shown, not chosen.
                    ImGui.BeginDisabled();
                    ImGui.Selectable(DisplayName(name), selected);
                    ImGui.EndDisabled();
                    continue;
                }

                if (ImGui.Selectable(DisplayName(name), selected) && !selected)
                    changed |= _binding.Select(name);
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered() && _binding.LastError is { } error)
            ImGui.SetTooltip(error);

        ImGui.SameLine(0f, style.ItemInnerSpacing.X);
        if (ImGui.Button(Icons.Save + _comboId + "-save", new Vector2(button, 0f)))
        {
            Array.Clear(_name);
            string suggestion = _binding.IsCustom || _binding.CurrentName == PresetLibrary<T>.DefaultName ? "" : _binding.CurrentName;
            Encoding.UTF8.GetBytes(suggestion, 0, Math.Min(suggestion.Length, 60), _name, 0);
            _focusName = true;
            ImGui.OpenPopup(_popupId);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(StageStr.PresetSaveAs));

        ImGui.SameLine(0f, style.ItemInnerSpacing.X);
        ImGui.BeginDisabled(!canDelete);
        if (ImGui.Button(Icons.Trash + _comboId + "-delete", new Vector2(button, 0f)))
            DeleteCurrent();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(StageLoc.Get(StageStr.PresetDelete));

        DrawSavePopup();
        return changed;
    }

    /// <summary>
    /// Deletes the selected saved preset. The values in use stay as they are — deleting a preset must not change the
    /// look of the viewports — and the dropdown shows <c>Custom</c> (they are no saved preset any more). Default and
    /// Custom cannot be deleted. Returns true when a preset was deleted.
    /// </summary>
    internal bool DeleteCurrent()
    {
        string name = _binding.CurrentName;
        if (name is PresetLibrary<T>.DefaultName or PresetLibrary<T>.CustomName)
            return false;
        return _binding.Delete(name);
    }

    private void DrawSavePopup()
    {
        if (!ImGui.BeginPopup(_popupId))
            return;
        ImGui.TextUnformatted(StageLoc.Get(StageStr.PresetSaveAs));
        if (_focusName)
        {
            ImGui.SetKeyboardFocusHere();
            _focusName = false;
        }

        bool enter;
        ImGui.SetNextItemWidth(220f * ImGui.GetStyle().FontScaleMain);
        fixed (byte* buffer = _name)
        {
            enter = ImGui.InputText("##name", buffer, (nuint)_name.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        }

        string name = NameText();
        bool valid = name.Length > 0 && name != PresetLibrary<T>.DefaultName && name != PresetLibrary<T>.CustomName &&
                     name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        if (_binding.LastError is { } error)
            ImGui.TextColored(Theme.Error, error);
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button(StageLoc.Get(StageStr.PresetSave)) || (enter && valid))
        {
            if (_binding.SaveAs(name))
                ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get(Str.MsgCancel)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private string NameText()
    {
        int length = Array.IndexOf(_name, (byte)0);
        return Encoding.UTF8.GetString(_name, 0, length < 0 ? _name.Length : length).Trim();
    }
}
