using System.Windows;
using System.Windows.Controls;
using AudioFool;
using AudioFool.Core.Models;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window nowplayinglines (session 68): the now-playing bar's four lines, title,
/// artist, "FLAC · 1,516 kbps · 96 kHz" and the quality tier, for a hi-res FLAC, a
/// CD FLAC, an MP3 and a DSF. Prints both lines and measures the text against the
/// cover and the bar against its height with nothing playing, so the extra line
/// can't make the bar taller. Renders &lt;out&gt;-&lt;n&gt;.png for each.
/// </summary>
internal static partial class Program
{
    private static int RunNowPlayingLines(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale)
    {
        var bar = (FrameworkElement)main.FindName("PlaybackBar");
        var details = (TextBlock)main.FindName("NowPlayingDetails");
        var tier = (TextBlock)main.FindName("NowPlayingFormat");
        var text = (FrameworkElement)details.Parent;
        var title = (TextBlock)main.FindName("NowPlayingTitle");

        Track[] tracks =
        [
            new() { FilePath = @"C:\x\a.flac", Title = "One Of These Days", Artist = "Pink Floyd", Kind = "FLAC", Bitrate = 1516, BitDepth = 24, SampleRate = 96000 },
            new() { FilePath = @"C:\x\b.flac", Title = "Time", Artist = "Pink Floyd", Kind = "FLAC", Bitrate = 912, BitDepth = 16, SampleRate = 44100 },
            new() { FilePath = @"C:\x\c.mp3", Title = "Learning To Fly", Artist = "Pink Floyd", Kind = "MP3", Bitrate = 320, SampleRate = 44100 },
            new() { FilePath = @"C:\x\d.dsf", Title = "Money", Artist = "Pink Floyd", Kind = "DSF", Bitrate = 5645, BitDepth = 1, SampleRate = 2822400 },
            // The longest likely: a 24-bit DSD128-rate DSF with a long title.
            new() { FilePath = @"C:\x\e.dsf", Title = "Shine On You Crazy Diamond (Parts I-V)", Artist = "Pink Floyd", Kind = "DSF", Bitrate = 11290, BitDepth = 1, SampleRate = 5644800 },
        ];

        vm.NowPlaying = null;
        Pump();
        var emptyHeight = bar.ActualHeight;
        Console.WriteLine($"nothing playing: bar {emptyHeight:0.##} px");

        var allOk = true;
        var stem = System.IO.Path.ChangeExtension(outPath, null);
        for (var i = 0; i < tracks.Length; i++)
        {
            vm.NowPlaying = tracks[i];
            Pump();
            var art = text.Parent is Grid grid ? grid.Children.OfType<Border>().First().ActualHeight : double.NaN;
            var fits = text.ActualHeight <= art + 0.5 && Math.Abs(bar.ActualHeight - emptyHeight) < 0.5;
            allOk &= fits && details.Text.Length > 0;
            Console.WriteLine($"  {(fits ? "OK" : "WRONG")}: '{details.Text}' / '{tier.Text}'; text {text.ActualHeight:0.##} px beside art {art:0.##} px "
                + $"(title top {title.TranslatePoint(new Point(), text).Y:0.##}); bar {bar.ActualHeight:0.##} px");
            Save(main, $"{stem}-{i + 1}.png", w, h, scale);
        }

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }
}
