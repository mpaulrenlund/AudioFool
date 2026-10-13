using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Art;
using AudioFool.Formatting;

namespace AudioFool.ViewModels;

/// <summary>
/// The PNG COVERS section of the Statistics window, on the way to converting them
/// to JPEG: songs with a PNG embedded, from the Extra Covers check's results (it
/// has no check of its own), and .png files in the songs' folders, looked for in
/// the background each time the window opens.
/// </summary>
public sealed partial class PngSectionViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _cancel = new();
    private PngFileCount? _files;
    private PngCoverStatistics _stats;

    public PngSectionViewModel(MainViewModel main)
    {
        _main = main;
        _stats = main.ComputePngStatistics();
        main.CoverResultsChanged += Main_CoverResultsChanged;
        Refresh();
        _ = CountFilesAsync();
    }

    public void Dispose()
    {
        _main.CoverResultsChanged -= Main_CoverResultsChanged;
        _cancel.Cancel();
    }

    [ObservableProperty]
    private IReadOnlyList<BarRow> _rows = [];

    [ObservableProperty]
    private string _summary = "";

    private async Task CountFilesAsync()
    {
        var tracks = _main.StatisticsTracks;
        var token = _cancel.Token;
        try
        {
            var files = await Task.Run(() => PngCoverStatistics.CountFiles(tracks, token), token);
            if (token.IsCancellationRequested)
                return;

            _files = files;
            _stats = _main.ComputePngStatistics(_files);
            Refresh();
        }
        catch (OperationCanceledException)
        {
            // The window closed first.
        }
    }

    private void Main_CoverResultsChanged(object? sender, EventArgs e)
    {
        _stats = _main.ComputePngStatistics(_files);
        Refresh();
    }

    private void Refresh()
    {
        var rows = new List<BarRow>();
        if (_stats.Checked > 0)
            rows.Add(EmbeddedRow());
        rows.Add(FilesRow());
        Rows = rows;

        Summary = _stats.Total == 0 ? "No tracks to check."
            : _stats.Checked == 0 ? "Embedded covers are read by the Extra Covers check above; run it to fill that row."
            : _stats.Unchecked > 0 ? $"Embedded covers: {_stats.Checked:N0} of {_stats.Total:N0} tracks read by the Extra Covers check."
            : $"All {_stats.Total:N0} tracks read.";
    }

    private BarRow EmbeddedRow()
    {
        if (_stats.EmbeddedTracks == 0)
            return new BarRow(PngCoverStatistics.EmbeddedLabel, "None", 0, null, isComplete: true);

        var share = (double)_stats.EmbeddedTracks / Math.Max(1, _stats.Checked);
        return new BarRow(PngCoverStatistics.EmbeddedLabel,
            $"{Count(_stats.EmbeddedAlbums, "album")} · {Count(_stats.EmbeddedTracks, "track")} · {Display.Size(_stats.EmbeddedBytes)}",
            share)
        {
            Filter = _stats.EmbeddedFilter,
        };
    }

    private BarRow FilesRow()
    {
        if (_files is not { } files)
            return new BarRow(PngCoverStatistics.FilesLabel, "Looking in the folders…", 0);

        if (files.Files == 0)
            return new BarRow(PngCoverStatistics.FilesLabel, "None", 0, null, isComplete: true);

        var share = (double)files.Folders / Math.Max(1, files.AllFolders);
        return new BarRow(PngCoverStatistics.FilesLabel,
            $"{Count(files.Albums, "album")} · {Count(files.Files, "file")} · {Display.Size(files.Bytes)}",
            share)
        {
            Filter = files.Filter,
        };
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
