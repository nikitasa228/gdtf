using System.Numerics;
using Candela.App.Docking;
using Candela.App.Files;
using Candela.App.Ui;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// Regressions found by exercising the editor against the UI requirements: dialogs and menus that left the window or
/// hid controls, labels cut in translated or scaled layouts.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class UxReviewTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void File_dialog_shows_all_its_controls_without_scrolling(float scale)
    {
        using var dir = new TempDir();
        dir.Write("show.mvr");
        using var ui = new HeadlessImGui(new Vector2(1600, 900));
        var dialog = new FileBrowserDialog(dir.File("recent-places.json"));
        dialog.Show(FileBrowserMode.Open, "Open", new[] { new FileFilter("MVR", "*.mvr") }, dir.Path, null, _ => { });
        for (int i = 0; i < 4; i++)
            ui.Frame(() => dialog.Draw(scale));
        var window = ImGuiP.FindWindowByName("Open###file-browser");
        Assert.False(window.IsNull);
        // A dialog that overflows grows a scroll bar and hides its last row (the "show hidden files" toggle did).
        Assert.Equal(0f, window.ScrollMax.Y);
        Assert.True(dialog.IsOpen);
    }

    [Fact]
    public void Tab_style_menu_opens_towards_the_inside_of_the_window()
    {
        var viewportPos = new Vector2(100, 50);
        var viewportSize = new Vector2(1600, 900);

        // A button at the right end of a tab bar (the right panel group): the menu's right edge is the button's.
        var (pos, pivot) = DockWorkspace.MenuPlacement(new Vector2(1640, 60), new Vector2(1660, 80), viewportPos, viewportSize);
        Assert.Equal(new Vector2(1660, 80), pos);
        Assert.Equal(new Vector2(1, 0), pivot);

        // A button on the left: the menu starts at the button.
        (pos, pivot) = DockWorkspace.MenuPlacement(new Vector2(400, 60), new Vector2(420, 80), viewportPos, viewportSize);
        Assert.Equal(new Vector2(400, 80), pos);
        Assert.Equal(Vector2.Zero, pivot);

        // The icon-column button at the bottom of a group: the menu opens upwards.
        (pos, pivot) = DockWorkspace.MenuPlacement(new Vector2(1500, 900), new Vector2(1540, 940), viewportPos, viewportSize);
        Assert.Equal(new Vector2(1540, 900), pos);
        Assert.Equal(new Vector2(1, 1), pivot);
    }

    [Theory]
    [InlineData(80f, 150f, 600f, 150f)] // short labels keep the grid's minimum
    [InlineData(200f, 150f, 600f, 216f)] // a long label widens the column (label + padding + gap)
    [InlineData(400f, 150f, 600f, 300f)] // but not beyond half of the panel
    [InlineData(400f, 150f, 200f, 150f)] // and never below the minimum in a very narrow panel
    [InlineData(0f, 120f, 600f, 120f)] // nothing measured yet
    public void Property_grid_label_column_fits_its_widest_label(float widest, float minimum, float available, float expected) =>
        Assert.Equal(expected, PropertyGrid.LabelColumnWidth(widest, minimum, available, 5f), 3);

    [Fact]
    public void Property_grid_widens_for_a_long_translated_label_after_one_frame()
    {
        using var ui = new HeadlessImGui(new Vector2(1200, 800));
        const string longLabel = "Exposure compensation (EV), a deliberately long label";
        float labelWidth = 0f, columnWidth = 0f;
        void Draw()
        {
            ImGui.SetNextWindowSize(new Vector2(900, 400));
            ImGui.Begin("grid-host");
            float tableStart = ImGui.GetCursorScreenPos().X;
            if (PropertyGrid.Begin("##grid", 100f))
            {
                PropertyGrid.Row("Short");
                ImGui.Button("value");
                PropertyGrid.Row(longLabel);
                columnWidth = ImGui.GetCursorScreenPos().X - tableStart; // where the editor column starts
                ImGui.Button("value");
                PropertyGrid.End();
            }

            labelWidth = ImGui.CalcTextSize(longLabel).X;
            ImGui.End();
        }

        for (int i = 0; i < 3; i++)
            ui.Frame(Draw);
        Assert.True(columnWidth >= labelWidth, $"label column {columnWidth} px is narrower than its label ({labelWidth} px)");
    }
}
