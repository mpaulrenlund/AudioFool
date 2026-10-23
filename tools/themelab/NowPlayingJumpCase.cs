using System.Windows.Controls;
using AudioFool;
using AudioFool.Core.Library;
using AudioFool.Core.Models;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window nowplayingjump (session 71): Ctrl+J over the real library.json (read
/// only). The song's own album, not the first of its name; the song selected in
/// the grid; a search or filter cleared only when it hides the song.
/// </summary>
internal static partial class Program
{
    private static int RunNowPlayingJump(MainWindow main, MainViewModel vm)
    {
        LoadRealLibrary(vm);
        Settle(300);
        var grid = (DataGrid)main.FindName("TrackGrid");

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var all = (AudioFool.Core.Library.LibraryCache.Load()?.Tracks ?? []).ToList();
        var library = LibraryScanner.Build(all);

        // An artist with two albums of the same title, if the library has one: the
        // song is taken from the one that isn't first.
        var twin = library.Artists
            .SelectMany(a => a.Albums.GroupBy(al => al.Title, SortRules.NameComparer).Where(g => g.Count() > 1).Select(g => g.Last()))
            .FirstOrDefault();
        var ordinary = library.Artists.Skip(library.Artists.Count / 2).First().Albums[0].Tracks[^1];

        void Jump(Track playing, string what, string? expectSearch, bool expectFiltered)
        {
            vm.NowPlaying = playing;
            vm.NavigateToNowPlaying();
            Settle(400);

            var shownAlbum = vm.SelectedAlbum?.Album;
            var holds = shownAlbum?.Tracks.Any(t => string.Equals(t.FilePath, playing.FilePath, StringComparison.OrdinalIgnoreCase)) == true;
            var selected = grid.SelectedItem is Track t2 && string.Equals(t2.FilePath, playing.FilePath, StringComparison.OrdinalIgnoreCase);
            Check(holds && selected && vm.SearchQuery == (expectSearch ?? "") && vm.IsFiltered == expectFiltered,
                $"{what}: album '{shownAlbum?.Title}' holds it {holds}, selected {selected}, search '{vm.SearchQuery}', filtered {vm.IsFiltered}");
        }

        // Somewhere else first, so the jump has to move.
        vm.SelectedArtist = vm.Artists[0];
        Settle(200);
        Jump(ordinary, "no search", null, false);

        vm.SelectedArtist = vm.Artists[0];
        vm.SearchQuery = "zzqxw no such thing";
        Settle(600);
        Jump(ordinary, "a search hiding it is cleared", null, false);

        var keep = ordinary.GroupingArtist;
        vm.SearchQuery = keep;
        Settle(600);
        Jump(ordinary, "a search showing it stays", keep, false);
        vm.SearchQuery = "";
        Settle(600);

        vm.LibraryFilter = new TrackFilter("Not the song", t => !string.Equals(t.FilePath, ordinary.FilePath, StringComparison.OrdinalIgnoreCase));
        Settle(300);
        Jump(ordinary, "a filter hiding it is cleared", null, false);

        vm.LibraryFilter = new TrackFilter("Its artist", t => SortRules.NameComparer.Equals(t.GroupingArtist, ordinary.GroupingArtist));
        Settle(300);
        Jump(ordinary, "a filter showing it stays", null, true);
        vm.LibraryFilter = null;
        Settle(300);

        if (twin is not null)
            Jump(twin.Tracks[^1], $"the second of two albums called '{twin.Title}'", null, false);
        else
            Console.WriteLine("  (no artist with two albums of one title in this library)");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }
}
