using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Models;
using AudioFool.Core.Scrobbling;

namespace AudioFool.ViewModels;

/// <summary>
/// Backs <see cref="ListeningHistoryWindow"/>: the user's Last.fm totals, their
/// top artists, albums and tracks for a chosen period, and what they played
/// last. Read-only; nothing is saved. Each period's lists are kept while the
/// window is open, so going back to one is instant.
/// </summary>
public sealed partial class ListeningHistoryViewModel : ObservableObject, IDisposable
{
    public const int TopCount = 10;
    public const int RecentCount = 20;

    private const ListeningPeriod FirstPeriod = ListeningPeriod.Year;

    private readonly LastFmApi? _api;
    private readonly string? _userName;
    private readonly IReadOnlyList<Track> _library;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<ListeningPeriod, TopLists> _periods = [];
    private ListeningMatch _match = ListeningMatch.Empty;
    private ListeningPeriod _period = FirstPeriod;

    private sealed record TopLists(IReadOnlyList<TopEntry> Artists, IReadOnlyList<TopEntry> Albums, IReadOnlyList<TopEntry> Tracks);

    /// <param name="api">Null when AudioFool isn't connected to Last.fm.</param>
    /// <param name="library">What the rows are matched against: the ticked folders' songs.</param>
    public ListeningHistoryViewModel(LastFmApi? api, string? userName, IReadOnlyList<Track> library, TimeProvider? clock = null)
    {
        _userName = string.IsNullOrWhiteSpace(userName) ? null : userName;
        _api = _userName is null ? null : api;
        _library = library;
        _clock = clock ?? TimeProvider.System;

        Title = _userName is null ? "Listening History" : $"Listening History · {_userName}";
        Periods = [.. ListeningPeriods.All.Select(p => new PeriodChoice(this, p, p == FirstPeriod))];
        _message = IsConnected ? "Loading from Last.fm…" : null;
    }

    public string Title { get; }

    public bool IsConnected => _api is not null;
    public bool IsNotConnected => !IsConnected;

    public IReadOnlyList<PeriodChoice> Periods { get; }

    /// <summary>The line in place of everything while the first answer is awaited, or when it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMessage))]
    private string? _message;

    public bool ShowsMessage => Message is not null;

    /// <summary>True once the first answer is in: the tiles, the switch and the sections show.</summary>
    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private IReadOnlyList<StatTile> _tiles = [];

    [ObservableProperty]
    private IReadOnlyList<ListeningRow> _topArtists = [];

    [ObservableProperty]
    private IReadOnlyList<ListeningRow> _topAlbums = [];

    [ObservableProperty]
    private IReadOnlyList<ListeningRow> _topTracks = [];

    [ObservableProperty]
    private IReadOnlyList<ListeningRow> _recent = [];

    /// <summary>What the three top sections say when they have no rows: loading, nothing played, or why not.</summary>
    [ObservableProperty]
    private string _topEmptyText = "";

    public string RecentEmptyText => "Nothing played yet.";

    /// <summary>The row the window was closed by clicking, if any.</summary>
    public ListeningRow? Chosen { get; set; }

    /// <summary>The window was closed by the link to the Last.fm window.</summary>
    public bool WantsLastFm { get; set; }

    /// <summary>Fetches the totals, the first period's lists and the recent plays, all at once.</summary>
    public async Task LoadAsync()
    {
        if (_api is not { } api || _userName is not { } user)
            return;

        var ct = _closing.Token;
        try
        {
            var match = Task.Run(() => new ListeningMatch(_library), ct);
            var profile = api.GetProfileAsync(user, ct);
            var recent = api.GetRecentTracksAsync(user, RecentCount, ct);
            var top = FetchPeriodAsync(api, user, FirstPeriod, ct);
            await Task.WhenAll(match, profile, recent, top);

            _match = await match;
            _periods[FirstPeriod] = await top;
            Tiles = MakeTiles(await profile);
            Recent = [.. (await recent).Select(MakeRecentRow)];
            ShowPeriod(FirstPeriod);
            Message = null;
            IsLoaded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (LastFmException ex)
        {
            Message = $"Couldn't load your listening history. {ex.Message.TrimEnd('.')}.";
        }
    }

    /// <summary>A period chosen on the switch: shown at once if already fetched, otherwise fetched now.</summary>
    internal async void SelectPeriod(ListeningPeriod period)
    {
        if (period == _period || _api is not { } api || _userName is not { } user)
            return;

        _period = period;
        if (_periods.ContainsKey(period))
        {
            ShowPeriod(period);
            return;
        }

        TopArtists = TopAlbums = TopTracks = [];
        TopEmptyText = "Loading…";

        var ct = _closing.Token;
        try
        {
            var lists = await FetchPeriodAsync(api, user, period, ct);
            _periods[period] = lists;
            if (_period == period)
                ShowPeriod(period);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (LastFmException ex)
        {
            if (_period == period)
                TopEmptyText = $"Couldn't load this period. {ex.Message.TrimEnd('.')}.";
        }
    }

    private static async Task<TopLists> FetchPeriodAsync(LastFmApi api, string user, ListeningPeriod period, CancellationToken ct)
    {
        var artists = api.GetTopArtistsAsync(user, period, TopCount, ct);
        var albums = api.GetTopAlbumsAsync(user, period, TopCount, ct);
        var tracks = api.GetTopTracksAsync(user, period, TopCount, ct);
        await Task.WhenAll(artists, albums, tracks);
        return new TopLists(await artists, await albums, await tracks);
    }

    private void ShowPeriod(ListeningPeriod period)
    {
        var lists = _periods[period];
        TopArtists = TopRows(lists.Artists, e => (ListeningKind.Artist, _match.Artist(e.Name)));
        TopAlbums = TopRows(lists.Albums, e => (ListeningKind.Album, _match.Album(e.Artist ?? "", e.Name)));
        TopTracks = TopRows(lists.Tracks, e => (ListeningKind.Song, _match.Song(e.Artist ?? "", e.Name)));
        TopEmptyText = period switch
        {
            ListeningPeriod.AllTime => "Nothing played yet.",
            ListeningPeriod.Month => "Nothing played in the last month.",
            _ => $"Nothing played in the last {ListeningPeriods.Label(period).ToLowerInvariant()}.",
        };
    }

    private static IReadOnlyList<ListeningRow> TopRows(IReadOnlyList<TopEntry> entries, Func<TopEntry, (ListeningKind, Track?)> find)
    {
        var most = entries.Count > 0 ? Math.Max(1, entries.Max(e => e.Plays)) : 1;
        return [.. entries.Select(e =>
        {
            var (kind, track) = find(e);
            return new ListeningRow(e.Name, Plays(e.Plays), e.Artist, (double)e.Plays / most, kind, track);
        })];
    }

    private ListeningRow MakeRecentRow(RecentPlay play)
    {
        var detail = string.IsNullOrWhiteSpace(play.Album) ? play.Artist : $"{play.Artist} · {play.Album}";
        var when = ListeningTime.Ago(play, _clock.GetUtcNow(), _clock.LocalTimeZone);
        return new ListeningRow(play.Track, when, detail, null, ListeningKind.Song, _match.Song(play.Artist, play.Track));
    }

    private IReadOnlyList<StatTile> MakeTiles(ListenerProfile p)
    {
        List<StatTile> tiles = [new($"{p.Scrobbles:N0}", "Scrobbles")];
        if (p.Registered is { } joined)
            tiles.Add(new(ListeningTime.Month(joined, _clock.LocalTimeZone), "Scrobbling since"));
        if (p.Artists is { } artists)
            tiles.Add(new($"{artists:N0}", "Artists"));
        if (p.Albums is { } albums)
            tiles.Add(new($"{albums:N0}", "Albums"));
        if (p.Tracks is { } tracks)
            tiles.Add(new($"{tracks:N0}", "Tracks"));
        return tiles;
    }

    private static string Plays(long n) => n == 1 ? "1 play" : $"{n:N0} plays";

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}

/// <summary>One word on the period switch. Choosing it asks the window's model for that period.</summary>
public sealed partial class PeriodChoice(ListeningHistoryViewModel owner, ListeningPeriod period, bool isSelected) : ObservableObject
{
    public ListeningPeriod Period { get; } = period;
    public string Label { get; } = ListeningPeriods.Label(period);

    [ObservableProperty]
    private bool _isSelected = isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            owner.SelectPeriod(Period);
    }
}

public enum ListeningKind { Artist, Album, Song }

/// <summary>
/// One Listening History row: a top-list line with a bar, or a recent play
/// without one (<paramref name="fill"/> null). It can be clicked when the
/// library holds what it names.
/// </summary>
public sealed class ListeningRow
{
    public ListeningRow(string label, string value, string? detail, double? fill, ListeningKind kind, Track? target)
    {
        Label = label;
        Value = value;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        Kind = kind;
        Target = target;
        HasBar = fill is not null;

        var f = Math.Clamp(fill is { } x && double.IsFinite(x) ? x : 0, 0, 1);
        Fill = new GridLength(f, GridUnitType.Star);
        Rest = new GridLength(1 - f, GridUnitType.Star);
    }

    public string Label { get; }
    public string Value { get; }
    public string? Detail { get; }
    public bool HasBar { get; }
    public GridLength Fill { get; }
    public GridLength Rest { get; }

    public ListeningKind Kind { get; }

    /// <summary>The library's song this row leads to: one by the artist, one on the album, or the song itself.</summary>
    public Track? Target { get; }

    public bool IsClickable => Target is not null;
}
