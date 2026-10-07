using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Analysis;
using AudioFool.Core.Models;
using AudioFool.Formatting;
using AudioFool.Services;
using AudioFool.Theming;

namespace AudioFool.ViewModels;

/// <summary>
/// Backs <see cref="AnalysisWindow"/>: decodes the track in the background as soon
/// as the window opens, then shows the spectrogram and AudioFool's opinion of it.
/// Reads the file only; nothing is written.
/// </summary>
public sealed partial class AnalysisViewModel : ObservableObject
{
    private readonly QualityClaim _claim;
    private readonly MainViewModel? _main;

    /// <param name="main">
    /// For the clear buttons: the library check's results and the user's
    /// clearances. Without it (a ThemeLab render) there are no buttons.
    /// </param>
    public AnalysisViewModel(Track track, MainViewModel? main = null)
    {
        Track = track;
        _claim = QualityClaim.From(track);
        _main = main;

        var artist = string.IsNullOrWhiteSpace(track.Artist) ? track.GroupingArtist : track.Artist;
        Subtitle = string.Join(" · ", new[] { artist, track.Album, _claim.Describe(), Display.Time(track.Duration) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (main is not null)
        {
            main.QualityClearancesChanged += Main_QualityChanged;
            main.QualityResultsChanged += Main_QualityChanged;
            RefreshClearing();
        }
    }

    /// <summary>Called when the window closes, so the main window stops updating it.</summary>
    public void Detach()
    {
        if (_main is null)
            return;

        _main.QualityClearancesChanged -= Main_QualityChanged;
        _main.QualityResultsChanged -= Main_QualityChanged;
    }

    private void Main_QualityChanged(object? sender, EventArgs e) => RefreshClearing();

    /// <summary>
    /// One button per Quality Check row this song is in, by this window's
    /// reading, the library check's, or a clearance already made: "Not Fake
    /// 24-bit", or "Put Back: Fake 24-bit" once cleared.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<QualityClearAction> _clearActions = [];

    /// <summary>"You cleared this from Fake 24-bit, ..."; null when it isn't cleared from anything.</summary>
    [ObservableProperty]
    private string? _clearedNote;

    private void RefreshClearing()
    {
        if (_main is null)
            return;

        var cleared = _main.Clearances.RowsClearedFor(Track);
        var rows = (Opinion?.Flags ?? [])
            .Concat(_main.CheckedQualityFlags(Track))
            .Select(QualityClearances.RowOf)
            .Concat(cleared)
            .Distinct()
            .OrderBy(f => QualityStatistics.Rows.ToList().FindIndex(r => r.Flag == f))
            .ToList();

        ClearActions =
        [
            .. rows.Select(flag => cleared.Contains(flag)
                ? new QualityClearAction($"Put Back: {QualityClearances.RowName(flag)}",
                    () => _main.PutBackQuality([Track], flag))
                : new QualityClearAction(QualityClearances.ClearLabel(flag),
                    () => _main.ClearQuality([Track], flag))),
        ];

        ClearedNote = cleared.Count == 0
            ? null
            : $"You cleared this from {Join(cleared.Select(QualityClearances.RowName).ToList())}, so the Quality Check leaves it out there.";

        static string Join(IReadOnlyList<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    partial void OnOpinionChanged(Opinion? value) => RefreshClearing();

    public Track Track { get; }

    public string WindowTitle => $"Analyze · {Track.DisplayTitle}";

    public string TrackTitle => Track.DisplayTitle;

    /// <summary>"Artist · Album · FLAC · 24-bit · 96 kHz · 6:14".</summary>
    public string Subtitle { get; }

    [ObservableProperty]
    private bool _isWorking = true;

    /// <summary>0 to 1 while the file is read.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "Reading the file…";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private SpectrumAnalysis? _analysis;

    [ObservableProperty]
    private BitmapSource? _picture;

    [ObservableProperty]
    private Opinion? _opinion;

    [ObservableProperty]
    private string _headline = "";

    [ObservableProperty]
    private IReadOnlyList<string> _explanations = [];

    /// <summary>The figures behind the opinion, for the footer.</summary>
    [ObservableProperty]
    private string _facts = "";

    public async Task RunAsync(CancellationToken cancel)
    {
        var tokens = TokenResources.Current!;
        var progress = new Progress<double>(p =>
        {
            Progress = p;
            Status = $"Reading the file… {p:0%}";
        });

        try
        {
            var path = Track.FilePath;
            var (analysis, picture) = await Task.Run(() =>
            {
                var a = TrackAnalyzer.Analyze(path, progress, cancel);
                return (a, SpectrogramImage.Render(a, tokens));
            }, cancel);

            var opinion = QualityOpinion.Form(analysis, _claim);

            Analysis = analysis;
            Picture = picture;
            Opinion = opinion;
            Headline = opinion.Headline;
            Explanations = opinion.Findings.Select(f => f.Explanation).Where(e => e.Length > 0).ToList();
            Facts = DescribeFacts(analysis, opinion);
        }
        catch (OperationCanceledException)
        {
            // The window closed first.
        }
        catch (AnalysisException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = $"The analysis failed: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>"Cutoff 20.2 kHz · Peak -0.1 dB · 16 of 24 bits used".</summary>
    private string DescribeFacts(SpectrumAnalysis analysis, Opinion opinion)
    {
        var parts = new List<string>
        {
            opinion.CutoffHz is { } hz ? $"Cutoff {QualityOpinion.Khz(hz)}" : "No cutoff",
            "Peak " + analysis.PeakSampleDb.ToString("0.0", CultureInfo.InvariantCulture).Replace("-", "−") + " dB",
        };

        if (analysis.UsedBits is { } used && _claim.BitDepth is { } stated)
            parts.Add($"{used} of {stated} bits used");

        if (_claim.IsDsd)
            parts.Add($"Read as {QualityOpinion.Khz(analysis.SampleRate)} PCM");

        return string.Join(" · ", parts);
    }
}

/// <summary>One of the Analyze window's clear buttons.</summary>
public sealed class QualityClearAction(string label, Action run)
{
    public string Label { get; } = label;

    public System.Windows.Input.ICommand Command { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(run);
}
