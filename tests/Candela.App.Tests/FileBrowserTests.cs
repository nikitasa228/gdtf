using Candela.App.Files;

namespace Candela.App.Tests;

public sealed class FileFilterTests
{
    [Theory]
    [InlineData("*.mvr", "show.mvr", true)]
    [InlineData("*.mvr", "SHOW.MVR", true)]
    [InlineData("*.mvr", "show.mvr.bak", false)]
    [InlineData("*.mvr", "mvr", false)]
    [InlineData("scene-??.candela", "scene-01.candela", true)]
    [InlineData("scene-??.candela", "scene-1.candela", false)]
    [InlineData("*a*b*", "xxaYYbzz", true)]
    [InlineData("*a*b*", "xxbYYazz", false)]
    [InlineData("*", "", true)]
    [InlineData("a*", "", false)]
    public void Wildcards(string pattern, string name, bool expected) => Assert.Equal(expected, FileFilter.Wildcard(pattern, name));

    [Fact]
    public void Parse_name_and_patterns()
    {
        var f = FileFilter.Parse("Scenes (Candela, MVR)|*.candela;*.mvr");
        Assert.Equal("Scenes (Candela, MVR)", f.Name);
        Assert.Equal(new[] { "*.candela", "*.mvr" }, f.Patterns);
        Assert.False(f.MatchesAll);
        Assert.Equal(".candela", f.DefaultExtension);
        Assert.True(f.Matches("Фокина.MVR"));
        Assert.False(f.Matches("readme.txt"));
        Assert.False(f.Matches(""));
        Assert.Equal("Scenes (Candela, MVR) (*.candela, *.mvr)", f.Label);
    }

    [Fact]
    public void Patterns_may_be_separated_by_commas_and_spaces()
    {
        var f = new FileFilter("Models", "*.glb, *.gltf *.obj;*.3ds");
        Assert.Equal(4, f.Patterns.Count);
        Assert.True(f.Matches("truss.OBJ"));
    }

    [Theory]
    [InlineData("All files|*")]
    [InlineData("All files|*.*")]
    [InlineData("All files|")]
    public void All_files(string text)
    {
        var f = FileFilter.Parse(text);
        Assert.True(f.MatchesAll);
        Assert.Null(f.DefaultExtension);
        Assert.True(f.Matches("anything"));
        Assert.Equal("All files", f.Label);
    }

    [Fact]
    public void Wildcard_extensions_have_no_default_extension()
    {
        Assert.Null(new FileFilter("x", "*.mv?").DefaultExtension);
        Assert.Null(new FileFilter("x", "show*").DefaultExtension);
    }
}

public sealed class FileBrowserModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly FileFilter[] Filters =
    {
        FileFilter.Parse("Scenes|*.candela;*.mvr"),
        FileFilter.Parse("Images|*.png;*.jpg"),
        FileFilter.Parse("All files|*"),
    };

    public FileBrowserModelTests()
    {
        _dir.Write("b.mvr", new string('x', 2048));
        _dir.Write("a10.candela", "1");
        _dir.Write("a2.candela", "12");
        _dir.Write("photo.PNG", "123");
        _dir.Write("notes.txt", "");
        _dir.Write(".hidden.mvr", "");
        _dir.Write("Shows/inner.mvr", "");
        _dir.Write("assets/x.png", "");
        Directory.CreateDirectory(_dir.File(".git"));
        File.SetLastWriteTimeUtc(_dir.File("a10.candela"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(_dir.File("a2.candela"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(_dir.File("b.mvr"), new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(_dir.File("Shows"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(_dir.File("assets"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose() => _dir.Dispose();

    private static string[] Names(FileBrowserModel m) => m.Entries.Select(e => e.Name).ToArray();

    [Fact]
    public void Lists_folders_first_then_matching_files_in_natural_order()
    {
        var m = new FileBrowserModel(FileBrowserMode.Open, Filters);
        Assert.True(m.Navigate(_dir.Path));
        Assert.Equal(new[] { "assets", "Shows", "a2.candela", "a10.candela", "b.mvr" }, Names(m));
        Assert.True(m.Entries[0].IsDirectory);
        Assert.Equal(2048, m.Entries.Single(e => e.Name == "b.mvr").Size);
        Assert.Equal("", m.Error);
    }

    [Fact]
    public void Filters_and_hidden_entries()
    {
        var m = new FileBrowserModel(FileBrowserMode.Open, Filters);
        m.Navigate(_dir.Path);
        m.SelectFilter(1);
        Assert.Equal(new[] { "assets", "Shows", "photo.PNG" }, Names(m));
        m.SelectFilter(2);
        Assert.Contains("notes.txt", Names(m));
        Assert.DoesNotContain(".hidden.mvr", Names(m));
        Assert.DoesNotContain(".git", Names(m));
        m.SetShowHidden(true);
        Assert.Contains(".hidden.mvr", Names(m));
        Assert.Contains(".git", Names(m));
        m.SelectFilter(17); // ignored
        Assert.Equal(2, m.FilterIndex);
    }

    [Fact]
    public void Sorting_by_size_and_date_keeps_folders_first()
    {
        var m = new FileBrowserModel(FileBrowserMode.Open, Filters);
        m.Navigate(_dir.Path);
        m.SortBy(FileSortColumn.Size, descending: true);
        Assert.Equal(new[] { "Shows", "assets", "b.mvr", "a2.candela", "a10.candela" }, Names(m));
        m.SortBy(FileSortColumn.Modified, descending: false);
        Assert.Equal(new[] { "assets", "Shows", "b.mvr", "a10.candela", "a2.candela" }, Names(m));
        m.SortBy(FileSortColumn.Modified); // same column again flips
        Assert.True(m.SortDescending);
    }

    [Fact]
    public void Navigation_up_and_errors()
    {
        var m = new FileBrowserModel(FileBrowserMode.Open, Filters);
        Assert.True(m.Navigate(Path.Combine(_dir.Path, "Shows")));
        Assert.Equal(new[] { "inner.mvr" }, Names(m));
        Assert.True(m.Up());
        Assert.Equal(Path.GetFullPath(_dir.Path), m.CurrentDirectory);
        Assert.False(m.Navigate(_dir.File("does-not-exist")));
        Assert.NotEqual("", m.Error);
        Assert.Equal(Path.GetFullPath(_dir.Path), m.CurrentDirectory); // unchanged
        Assert.False(m.Navigate("   "));
        Assert.True(m.Refresh());
    }

    [Fact]
    public void Open_resolves_existing_files_only()
    {
        var m = new FileBrowserModel(FileBrowserMode.Open, Filters);
        m.Navigate(_dir.Path);
        Assert.Null(m.Resolve());
        m.FileName = "b.mvr";
        Assert.Equal(_dir.File("b.mvr"), m.Resolve());
        m.FileName = "Shows/inner.mvr";
        Assert.Equal(_dir.File("Shows/inner.mvr"), m.Resolve());
        m.FileName = "missing.mvr";
        Assert.Null(m.Resolve());
        Assert.Contains("does not exist", m.Error);
    }

    [Fact]
    public void Save_adds_the_default_extension_and_detects_overwrites()
    {
        var m = new FileBrowserModel(FileBrowserMode.Save, Filters);
        m.Navigate(_dir.Path);
        m.FileName = "new show";
        Assert.Equal(_dir.File("new show.candela"), m.Resolve());
        m.FileName = "b.mvr"; // matches the filter: kept as typed
        string? path = m.Resolve();
        Assert.Equal(_dir.File("b.mvr"), path);
        Assert.True(FileBrowserModel.WouldOverwrite(path!));
        Assert.False(FileBrowserModel.WouldOverwrite(_dir.File("new show.candela")));
    }

    [Fact]
    public void Save_rejects_folders_and_missing_parents()
    {
        var m = new FileBrowserModel(FileBrowserMode.Save, new[] { FileFilter.Parse("All|*") });
        m.Navigate(_dir.Path);
        m.FileName = "Shows";
        Assert.Null(m.Resolve());
        Assert.Contains("folder", m.Error);
        m.FileName = "nowhere/x.candela";
        Assert.Null(m.Resolve());
        m.FileName = "";
        Assert.Null(m.Resolve());
        m.FileName = "Shows/";
        Assert.Null(m.Resolve());
    }

    [Fact]
    public void Changing_the_filter_while_saving_changes_the_extension()
    {
        var m = new FileBrowserModel(FileBrowserMode.Save, Filters);
        m.Navigate(_dir.Path);
        m.FileName = "render.candela";
        m.SelectFilter(1);
        Assert.Equal("render.png", m.FileName);
    }

    [Fact]
    public void Folder_mode_lists_folders_only()
    {
        var m = new FileBrowserModel(FileBrowserMode.SelectFolder, Filters);
        m.Navigate(_dir.Path);
        Assert.Equal(new[] { "assets", "Shows" }, Names(m));
        Assert.Equal(Path.GetFullPath(_dir.Path), m.Resolve());
        m.FileName = "assets";
        Assert.Equal(_dir.File("assets"), m.Resolve());
        m.FileName = "b.mvr";
        Assert.Null(m.Resolve());
    }

    [Fact]
    public void Recent_places_are_kept_newest_first_and_persisted()
    {
        string file = _dir.File("config/recent-places.json");
        var m = new FileBrowserModel(FileBrowserMode.Open, Filters, file);
        m.AddRecentPlace(_dir.File("b.mvr"));
        m.AddRecentPlace(_dir.File("Shows"));
        m.AddRecentPlace(_dir.File("a2.candela"));
        Assert.Equal(new[] { _dir.Path, _dir.File("Shows") }, m.RecentPlaces);
        var reloaded = new FileBrowserModel(FileBrowserMode.Open, Filters, file);
        Assert.Equal(m.RecentPlaces, reloaded.RecentPlaces);
        for (int i = 0; i < 15; i++)
            m.AddRecentPlace(Path.Combine(_dir.Path, "f" + i, "x.mvr"));
        Assert.Equal(FileBrowserModel.MaxRecentPlaces, m.RecentPlaces.Count);
    }

    [Fact]
    public void A_damaged_recent_places_file_is_ignored()
    {
        string file = _dir.Write("recent.json", "[[[");
        var m = new FileBrowserModel(FileBrowserMode.Open, Filters, file);
        Assert.Empty(m.RecentPlaces);
    }

    [Fact]
    public void Standard_places_exist()
    {
        var places = FileBrowserModel.StandardPlaces("Home", "Documents", "Desktop", "Working folder");
        Assert.NotEmpty(places);
        Assert.All(places, p => Assert.True(Directory.Exists(p.Path)));
        Assert.Equal(places.Count, places.Select(p => p.Path).Distinct().Count());
        Assert.Contains(places, p => p.Label == "Working folder");
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 * 1024, "3 TB")]
    public void Sizes(long bytes, string expected) => Assert.Equal(expected, FileBrowserModel.FormatSize(bytes));

    [Theory]
    [InlineData("file2", "file10", -1)]
    [InlineData("File10", "file2", 1)]
    [InlineData("a", "A", 0)]
    [InlineData("x007", "x7", 0)]
    [InlineData("abc", "abcd", -1)]
    public void Natural_compare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(FileBrowserModel.NaturalCompare(a, b)));
}
