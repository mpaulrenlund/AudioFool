using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Art;

namespace AudioFool.ViewModels;

/// <summary>One cover in the results grid. The preview arrives after the size does.</summary>
public sealed partial class ArtCandidateItem : ObservableObject
{
    public ArtCandidateItem(ArtCandidate candidate) => Candidate = candidate;

    public ArtCandidate Candidate { get; }

    public string SizeText => $"{Candidate.Width:N0} × {Candidate.Height:N0}";

    public string Source => Candidate.Source;

    public string Title => Candidate.Title;

    [ObservableProperty]
    private BitmapSource? _preview;
}

/// <summary>
/// Backs <see cref="ArtSearchWindow"/>: searches as soon as it opens, shows each
/// cover as it is measured, and downloads the chosen one in full.
/// </summary>
public sealed partial class ArtSearchViewModel : ObservableObject
{
    private readonly OnlineArtSearch _search;
    private CancellationTokenSource? _running;

    public ArtSearchViewModel(OnlineArtSearch search, string artist, string album)
    {
        _search = search;
        _artist = artist;
        _album = album;
    }

    [ObservableProperty]
    private string _artist;

    [ObservableProperty]
    private string _album;

    public ObservableCollection<ArtCandidateItem> Results { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse))]
    private ArtCandidateItem? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    public bool CanUse => Selected is not null && !IsBusy;

    /// <summary>The full-size JPEG, once <see cref="DownloadSelectedAsync"/> succeeds.</summary>
    public byte[]? ChosenBytes { get; private set; }

    [RelayCommand]
    public async Task SearchAsync()
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        Results.Clear();
        Selected = null;
        IsBusy = true;
        Status = "Searching…";

        var dispatcher = Application.Current.Dispatcher;
        try
        {
            var summary = await _search.SearchAsync(Artist.Trim(), Album.Trim(), candidate =>
                dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested)
                        return;
                    var item = new ArtCandidateItem(candidate);
                    Results.Insert(InsertionIndex(candidate), item);
                    Status = $"Searching… {Results.Count} found";
                    _ = LoadPreviewAsync(item, cts.Token);
                }), cts.Token);

            // Through the dispatcher, so it lands after every result still queued
            // there instead of being overwritten by one of them.
            await dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested)
                    Status = Describe(summary);
            });
        }
        catch (OperationCanceledException)
        {
            // A newer search or the window closing replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
                IsBusy = false;
        }
    }

    /// <summary>
    /// Covers arrive in whatever order they finish measuring; keep the grid sorted
    /// by match (artist and album before artist only), then by size.
    /// </summary>
    private int InsertionIndex(ArtCandidate candidate)
    {
        static (int, long) Key(ArtCandidate c) => (c.Relevance, (long)c.Width * c.Height);

        var key = Key(candidate);
        var index = 0;
        while (index < Results.Count && Key(Results[index].Candidate).CompareTo(key) >= 0)
            index++;
        return index;
    }

    private static string Describe(ArtSearchSummary summary)
    {
        var parts = new List<string>
        {
            summary.Shown switch
            {
                0 => $"No covers of at least {OnlineArtSearch.MinimumSize:N0} × {OnlineArtSearch.MinimumSize:N0} found",
                1 => "1 cover",
                var n => $"{n} covers",
            },
        };

        var hidden = new List<string>();
        if (summary.TooSmall > 0)
            hidden.Add($"{summary.TooSmall} under {OnlineArtSearch.MinimumSize:N0} px");
        if (summary.NotJpeg > 0)
            hidden.Add($"{summary.NotJpeg} not JPEG");
        if (summary.Unrelated > 0)
            hidden.Add($"{summary.Unrelated} by other artists");
        if (hidden.Count > 0)
            parts.Add("hidden: " + string.Join(", ", hidden));
        if (summary.Unreachable > 0)
            parts.Add($"{summary.Unreachable} could not be reached");
        parts.AddRange(summary.Notes);

        return string.Join(" · ", parts);
    }

    private async Task LoadPreviewAsync(ArtCandidateItem item, CancellationToken ct)
    {
        try
        {
            var bytes = await _search.DownloadPreviewAsync(item.Candidate, ct);
            item.Preview = await Task.Run(() => Decode(bytes, 320), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                     or NotSupportedException or IOException or ArgumentException)
        {
            // The size is already shown; a missing thumbnail leaves the placeholder.
        }
    }

    /// <summary>Downloads the selected cover. False, with the reason in <see cref="Status"/>, on failure.</summary>
    public async Task<bool> DownloadSelectedAsync()
    {
        if (Selected is not { } item)
            return false;

        Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        IsBusy = true;
        Status = $"Downloading {item.SizeText} cover…";

        try
        {
            ChosenBytes = await _search.DownloadAsync(item.Candidate, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            Status = $"Couldn't download that cover: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel()
    {
        _running?.Cancel();
        _running = null;
    }

    public static BitmapSource Decode(byte[] bytes, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
