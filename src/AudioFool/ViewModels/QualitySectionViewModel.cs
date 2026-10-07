using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Analysis;
using AudioFool.Formatting;

namespace AudioFool.ViewModels;

/// <summary>
/// The QUALITY CHECK section of the Statistics window: one row per problem the
/// library check found, a line saying how much has been checked, and a button
/// to start or stop the check.
/// <para>
/// Unlike the rest of Statistics it is live. The check runs in
/// <see cref="MainViewModel"/> and carries on after the window closes; while the
/// window is open, its progress and each batch of saved results show here.
/// </para>
/// </summary>
public sealed partial class QualitySectionViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Four threads checked about 42 tracks a second off the library's USB SSD;
    /// a little under, for the estimate.
    /// </summary>
    private const double TracksPerSecond = 40;

    private readonly MainViewModel _main;
    private QualityStatistics _stats;

    public QualitySectionViewModel(MainViewModel main)
    {
        _main = main;
        _stats = main.ComputeQualityStatistics();
        main.PropertyChanged += Main_PropertyChanged;
        main.QualityResultsChanged += Main_QualityResultsChanged;
        main.QualityClearancesChanged += Main_QualityResultsChanged;
        Refresh();
    }

    public void Dispose()
    {
        _main.PropertyChanged -= Main_PropertyChanged;
        _main.QualityResultsChanged -= Main_QualityResultsChanged;
        _main.QualityClearancesChanged -= Main_QualityResultsChanged;
    }

    /// <summary>
    /// "Cleared by you", under the problem rows in grey: it isn't a problem. One
    /// row or none, as a list so it uses the same row template.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<BarRow> _clearedRows = [];

    public bool HasClearedRows => ClearedRows.Count > 0;

    partial void OnClearedRowsChanged(IReadOnlyList<BarRow> value) => OnPropertyChanged(nameof(HasClearedRows));

    /// <summary>One bar per problem; empty until something has been checked.</summary>
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
        if (_main.IsCheckingQuality)
            _main.StopQualityCheck();
        else
            _main.StartQualityCheck();
    }

    private void Main_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsCheckingQuality) or nameof(MainViewModel.QualityProgress))
            UpdateStatus();
    }

    private void Main_QualityResultsChanged(object? sender, EventArgs e)
    {
        _stats = _main.ComputeQualityStatistics();
        Refresh();
    }

    private void Refresh()
    {
        Rows = _stats.Checked == 0 ? [] : [.. _stats.Gaps.Select(Row)];
        ClearedRows = _stats.Cleared == 0 || _stats.ClearedFilter is null
            ? []
            :
            [
                new BarRow(QualityStatistics.ClearedLabel, Plural(_stats.Cleared),
                    (double)_stats.Cleared / Math.Max(1, _stats.Checked),
                    "Tracks you've said are genuine; right-click one to put it back")
                {
                    Filter = _stats.ClearedFilter,
                    ToolTip = _stats.Cleared == 1 ? "Show this track in the library" : $"Show these {_stats.Cleared:N0} tracks in the library",
                },
            ];
        UpdateStatus();
    }

    private BarRow Row(QualityGap gap)
    {
        if (gap.Tracks == 0)
            return new BarRow(gap.Label, "None", 0, gap.Note, isComplete: true);

        // Shares of the checked tracks, so a part-done check isn't understated.
        var share = (double)gap.Tracks / Math.Max(1, _stats.Checked);
        return new BarRow(gap.Label, $"{Plural(gap.Tracks)} · {Display.Percent(share)}", share, gap.Note)
        {
            Filter = gap.Filter,
            ToolTip = gap.Tracks == 1 ? "Show this track in the library" : $"Show these {gap.Tracks:N0} tracks in the library",
        };
    }

    private void UpdateStatus()
    {
        if (_main.IsCheckingQuality)
        {
            var p = _main.QualityProgress;
            var found = p.Flagged > 0 ? $", {p.Flagged:N0} found so far" : "";
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
            ButtonText = "Check quality";
            CanToggle = false;
        }
        else if (_stats.Checked == 0)
        {
            Summary = $"Not checked yet. The check reads three short slices of every track, {estimate} for this library, "
                + "while you keep listening, and remembers what it found.";
            ButtonText = "Check quality";
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

    private static string Plural(int n) => n == 1 ? "1 track" : $"{n:N0} tracks";
}
