using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Art;
using AudioFool.Formatting;

namespace AudioFool.ViewModels;

/// <summary>
/// The EXTRA COVERS section of the Statistics window: the songs carrying more than
/// one embedded picture, a line saying how much has been read, and a button to
/// start or stop the check. Live, as the Quality Check is: the check runs in
/// <see cref="MainViewModel"/> and carries on after the window closes.
/// </summary>
public sealed partial class CoverSectionViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Four threads read 26,860 tracks in 68 s (393 a second) off the library's
    /// USB SSD; under that, for the estimate, since playback shares the drive.
    /// </summary>
    private const double TracksPerSecond = 300;

    private readonly MainViewModel _main;
    private CoverStatistics _stats;

    public CoverSectionViewModel(MainViewModel main)
    {
        _main = main;
        _stats = main.ComputeCoverStatistics();
        main.PropertyChanged += Main_PropertyChanged;
        main.CoverResultsChanged += Main_CoverResultsChanged;
        Refresh();
    }

    public void Dispose()
    {
        _main.PropertyChanged -= Main_PropertyChanged;
        _main.CoverResultsChanged -= Main_CoverResultsChanged;
    }

    /// <summary>The one row, or none until something has been read.</summary>
    [ObservableProperty]
    private IReadOnlyList<BarRow> _rows = [];

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _buttonText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    private bool _canToggle;

    public bool HasRows => Rows.Count > 0;

    partial void OnRowsChanged(IReadOnlyList<BarRow> value) => OnPropertyChanged(nameof(HasRows));

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private void Toggle()
    {
        if (_main.IsCheckingCovers)
            _main.StopCoverCheck();
        else
            _main.StartCoverCheck();
    }

    private void Main_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsCheckingCovers) or nameof(MainViewModel.CoverProgress))
            UpdateStatus();
    }

    private void Main_CoverResultsChanged(object? sender, EventArgs e)
    {
        _stats = _main.ComputeCoverStatistics();
        Refresh();
    }

    private void Refresh()
    {
        Rows = _stats.Checked == 0 ? [] : [Row()];
        UpdateStatus();
    }

    private BarRow Row()
    {
        const string note = "Right-click an album there: Keep Best Cover";
        if (_stats.Tracks == 0)
            return new BarRow(CoverStatistics.Label, "None", 0, null, isComplete: true);

        var share = (double)_stats.Tracks / Math.Max(1, _stats.Checked);
        var albums = _stats.Albums == 1 ? "1 album" : $"{_stats.Albums:N0} albums";
        var tracks = _stats.Tracks == 1 ? "1 track" : $"{_stats.Tracks:N0} tracks";
        return new BarRow(CoverStatistics.Label, $"{albums} · {tracks} · {Display.Size(_stats.Freeable)} to free", share, note)
        {
            Filter = _stats.Filter,
            ToolTip = $"Show {(_stats.Albums == 1 ? "this album" : $"these {_stats.Albums:N0} albums")} in the library",
        };
    }

    private void UpdateStatus()
    {
        if (_main.IsCheckingCovers)
        {
            var p = _main.CoverProgress;
            var found = p.Found > 0 ? $", {p.Found:N0} with extra covers so far" : "";
            Summary = $"Checking {p.Done:N0} of {p.Total:N0} tracks{found}. You can close this window; the check carries on, with progress in the status bar.";
            ButtonText = "Stop checking";
            CanToggle = true;
            return;
        }

        var minutes = Math.Max(1, (int)Math.Round(_stats.Unchecked / TracksPerSecond / 60));
        var estimate = minutes == 1 ? "about a minute" : $"about {minutes} minutes";

        if (_stats.Total == 0)
        {
            Summary = "No tracks to check.";
            ButtonText = "Check covers";
            CanToggle = false;
        }
        else if (_stats.Checked == 0)
        {
            Summary = $"Not checked yet. The check reads the pictures embedded in every track, {estimate} for this library, "
                + "and finds those carrying the cover more than once. It remembers what it found.";
            ButtonText = "Check covers";
            CanToggle = true;
        }
        else if (_stats.Unchecked > 0)
        {
            Summary = $"{_stats.Checked:N0} of {_stats.Total:N0} tracks checked. "
                + $"{_stats.Unchecked:N0} are new, changed or not reached yet ({estimate}).";
            ButtonText = "Check the rest";
            CanToggle = true;
        }
        else
        {
            Summary = $"All {_stats.Total:N0} tracks checked.";
            ButtonText = "All checked";
            CanToggle = false;
        }
    }
}
