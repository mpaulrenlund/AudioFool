using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Art;

namespace AudioFool.ViewModels;

/// <summary>
/// The CUT-OFF COVERS section of the Statistics window (session 70): songs carrying
/// a picture that stops before its end, from the Extra Covers check's results (it
/// has no check of its own), and the folder cover files the songs show, read in
/// the background each time the window opens. Built as PNG COVERS is.
/// </summary>
public sealed partial class CutOffSectionViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _cancel = new();
    private CutOffFileCount? _files;
    private CutOffCoverStatistics _stats;

    public CutOffSectionViewModel(MainViewModel main)
    {
        _main = main;
        _stats = main.ComputeCutOffStatistics();
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
            var files = await Task.Run(() => CutOffCoverStatistics.CountFiles(tracks, token), token);
            if (token.IsCancellationRequested)
                return;

            _files = files;
            _stats = _main.ComputeCutOffStatistics(_files);
            Refresh();
        }
        catch (OperationCanceledException)
        {
            // The window closed first.
        }
    }

    private void Main_CoverResultsChanged(object? sender, EventArgs e)
    {
        _stats = _main.ComputeCutOffStatistics(_files);
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
            return new BarRow(CutOffCoverStatistics.EmbeddedLabel, "None", 0, null, isComplete: true);

        var share = (double)_stats.EmbeddedTracks / Math.Max(1, _stats.Checked);
        return new BarRow(CutOffCoverStatistics.EmbeddedLabel,
            $"{Count(_stats.EmbeddedAlbums, "album")} · {Count(_stats.EmbeddedTracks, "track")}",
            share)
        {
            Filter = _stats.EmbeddedFilter,
        };
    }

    private BarRow FilesRow()
    {
        if (_files is not { } files)
            return new BarRow(CutOffCoverStatistics.FilesLabel, "Reading the cover files…", 0);

        if (files.Files == 0)
            return new BarRow(CutOffCoverStatistics.FilesLabel, "None", 0, null, isComplete: true);

        var share = (double)files.Files / Math.Max(1, files.AllFiles);
        return new BarRow(CutOffCoverStatistics.FilesLabel,
            $"{Count(files.Albums, "album")} · {Count(files.Files, "file")}",
            share)
        {
            Filter = files.Filter,
        };
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
