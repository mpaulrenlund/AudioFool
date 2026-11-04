using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using AudioFool.Core.Genius;
using AudioFool.Core.Models;

namespace AudioFool.ViewModels;

/// <summary>
/// Backs <see cref="GeniusWindow"/>: what Genius has on one library song, its
/// write-up, questions and answers, explanations of the lyrics, credits,
/// connections and comments. Read-only. The header is the library's own song
/// (its tags and cover); everything under it is the Genius page.
/// <para>
/// One window shows one song at a time: <see cref="ShowAsync"/> again switches
/// it to another, dropping whatever the last one was still waiting for.
/// </para>
/// </summary>
public sealed partial class GeniusViewModel : ObservableObject, IDisposable
{
    /// <summary>How many songs of one kind of connection are named before "and 7 more".</summary>
    public const int ConnectionsShown = 5;

    public const string LookingUp = "Looking up on Genius…";
    public const string NoMatch = "Genius has no page that matches this song.";
    public const string NoTitle = "This song has no title to look up on Genius.";
    public const string EmptyPage = "Genius's page for this song has nothing on it yet.";

    private readonly GeniusLookup _lookup;
    private readonly Func<Track, Task<BitmapSource?>> _loadArt;
    private CancellationTokenSource _showing = new();
    private long? _songId;
    private int? _nextCommentPage;
    private int _commentTotal;
    private bool _loadingMore;

    /// <param name="loadArt">The library cover for a song, at the album header's size.</param>
    public GeniusViewModel(GeniusLookup lookup, Func<Track, Task<BitmapSource?>> loadArt)
    {
        _lookup = lookup;
        _loadArt = loadArt;
        Comments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasComments));
    }

    /// <summary>The song the window is about.</summary>
    [ObservableProperty]
    private Track? _track;

    [ObservableProperty]
    private string _title = "Genius";

    [ObservableProperty]
    private string _songTitle = "";

    [ObservableProperty]
    private string _artist = "";

    /// <summary>"OK Computer · 1997", from the song's own tags.</summary>
    [ObservableProperty]
    private string? _detailLine;

    [ObservableProperty]
    private BitmapSource? _art;

    /// <summary>The Genius page's address once one is loaded; the header's link opens it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage))]
    private string? _pageUrl;

    public bool HasPage => PageUrl is not null;

    /// <summary>The line in place of the page: looking it up, no match, or why it couldn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMessage))]
    private string? _message;

    public bool ShowsMessage => Message is not null;

    /// <summary>With no match, Genius's nearest results; choosing one loads it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions))]
    private IReadOnlyList<GeniusChoice> _suggestions = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Parts of the page Genius didn't send this time, said in a line; null when nothing is missing.</summary>
    [ObservableProperty]
    private string? _missingText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAbout))]
    private string? _about;

    public bool HasAbout => About is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswers))]
    private IReadOnlyList<GeniusEntry> _answers = [];

    public bool HasAnswers => Answers.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnnotations))]
    private IReadOnlyList<GeniusEntry> _annotations = [];

    public bool HasAnnotations => Annotations.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCredits))]
    private IReadOnlyList<GeniusCreditRow> _credits = [];

    public bool HasCredits => Credits.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnections))]
    private IReadOnlyList<GeniusCreditRow> _connections = [];

    public bool HasConnections => Connections.Count > 0;

    /// <summary>The comments shown so far; "Show 50 more" adds the next page under them.</summary>
    public ObservableCollection<GeniusEntry> Comments { get; } = [];

    public bool HasComments => Comments.Count > 0;

    /// <summary>"34 comments", at the right of the section's header.</summary>
    [ObservableProperty]
    private string _commentCount = "";

    /// <summary>"Show 50 more" while Genius has more comments; null after the last.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreComments))]
    private string? _moreCommentsText;

    public bool HasMoreComments => MoreCommentsText is not null;

    /// <summary>
    /// Looks the song up and fills the window: the page that is the song, or
    /// the nearest results when none is. Whatever the window showed before is
    /// cleared at once, and a lookup still running for it is dropped.
    /// </summary>
    public async Task ShowAsync(Track track)
    {
        var ct = Restart();

        Track = track;
        SongTitle = track.Title.Trim().Length > 0 ? track.Title.Trim() : Path.GetFileNameWithoutExtension(track.FilePath);
        Artist = (track.Artist.Trim().Length > 0 ? track.Artist : track.AlbumArtist).Trim();
        DetailLine = JoinDetails(track.Album, track.Year?.ToString());
        Title = $"{SongTitle} · Genius";
        ClearPage();
        Art = null;
        _ = ShowArtAsync(track, ct);

        if (track.Title.Trim().Length == 0)
        {
            Message = NoTitle;
            return;
        }

        Message = LookingUp;
        try
        {
            var find = await _lookup.FindAsync(track.Artist, track.AlbumArtist, track.Title, ct);
            if (ct.IsCancellationRequested)
                return;

            if (find.Match is { } match)
            {
                await LoadPageAsync(match.Id, ct);
                return;
            }

            Message = NoMatch;
            Suggestions = [.. find.Suggestions.Select(h => new GeniusChoice(h))];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (GeniusException ex) when (!ct.IsCancellationRequested)
        {
            Message = Failed(ex);
        }
    }

    /// <summary>One of the nearest results, chosen: its page fills the window under the same header.</summary>
    public async Task ChooseAsync(GeniusChoice choice)
    {
        var ct = Restart();
        ClearPage();
        Message = LookingUp;
        try
        {
            await LoadPageAsync(choice.Hit.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (GeniusException ex) when (!ct.IsCancellationRequested)
        {
            Message = Failed(ex);
        }
    }

    /// <summary>The next page of comments, added under the ones shown.</summary>
    public async Task ShowMoreCommentsAsync()
    {
        if (_loadingMore || _songId is not { } id || _nextCommentPage is not { } next)
            return;

        var ct = _showing.Token;
        _loadingMore = true;
        try
        {
            var page = await _lookup.Client.GetCommentsAsync(id, next, ct);
            if (ct.IsCancellationRequested || _songId != id)
                return;

            foreach (var c in page.Comments)
                Comments.Add(CommentEntry(c));
            _nextCommentPage = page.Comments.Count > 0 ? page.NextPage : null;
            MoreCommentsText = MoreText();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (GeniusException) when (!ct.IsCancellationRequested)
        {
            MoreCommentsText = "Couldn't load more comments. Try again";
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private async Task LoadPageAsync(long id, CancellationToken ct)
    {
        var result = await _lookup.LoadAsync(id, ct);
        if (ct.IsCancellationRequested)
            return;

        var page = result.Page;
        var song = page.Song;
        _songId = song.Id;
        PageUrl = song.Url.Length > 0 ? song.Url : null;
        About = song.About;
        Answers = [.. page.Answers.Select(a => new GeniusEntry(a.Question, a.Answer, null, false))];
        Annotations = [.. page.Annotations.Select(n => new GeniusEntry(n.Fragment, n.Body, n.Verified ? "Verified" : null, true))];
        Credits = [.. song.Credits.Select(c => new GeniusCreditRow(c.Label, string.Join(", ", c.Names)))];
        Connections = [.. song.Connections.Select(c => new GeniusCreditRow(c.Label, ConnectionText(c.Songs)))];

        Comments.Clear();
        foreach (var c in page.Comments.Comments)
            Comments.Add(CommentEntry(c));
        _commentTotal = Math.Max(page.Comments.Total, Comments.Count);
        _nextCommentPage = page.Comments.NextPage;
        CommentCount = _commentTotal == 1 ? "1 comment" : $"{_commentTotal:N0} comments";
        MoreCommentsText = MoreText();

        MissingText = result.Missing.Count == 0 ? null
            : $"Genius didn't send the {JoinNames(result.Missing)} this time. Open the song again to try once more.";
        Suggestions = [];

        var empty = About is null && Answers.Count == 0 && Annotations.Count == 0 && Credits.Count == 0
            && Connections.Count == 0 && Comments.Count == 0 && result.Missing.Count == 0;
        Message = empty ? EmptyPage : null;
    }

    private async Task ShowArtAsync(Track track, CancellationToken ct)
    {
        try
        {
            var art = await _loadArt(track);
            if (!ct.IsCancellationRequested)
                Art = art;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    private CancellationToken Restart()
    {
        _showing.Cancel();
        _showing.Dispose();
        _showing = new CancellationTokenSource();
        _loadingMore = false;
        return _showing.Token;
    }

    private void ClearPage()
    {
        _songId = null;
        _nextCommentPage = null;
        _commentTotal = 0;
        PageUrl = null;
        Message = null;
        MissingText = null;
        Suggestions = [];
        About = null;
        Answers = [];
        Annotations = [];
        Credits = [];
        Connections = [];
        Comments.Clear();
        CommentCount = "";
        MoreCommentsText = null;
    }

    private string? MoreText()
    {
        if (_nextCommentPage is null)
            return null;
        var left = _commentTotal - Comments.Count;
        var next = left > 0 ? Math.Min(GeniusClient.CommentsPerPage, left) : GeniusClient.CommentsPerPage;
        return $"Show {next:N0} more";
    }

    internal static GeniusEntry CommentEntry(GeniusComment c)
    {
        var votes = c.Votes is 1 or -1 ? $"{c.Votes} vote" : $"{c.Votes:N0} votes";
        var figure = c.Posted is { } at
            ? $"{at.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)} · {votes}"
            : votes;
        return new GeniusEntry(c.Author, c.Body, figure, false);
    }

    /// <summary>One song per line, "Title · Artist", the first five, then "and 7 more".</summary>
    internal static string ConnectionText(IReadOnlyList<GeniusHit> songs)
    {
        var lines = songs.Take(ConnectionsShown).Select(s => s.Artist.Length > 0 ? $"{s.Title} · {s.Artist}" : s.Title).ToList();
        if (songs.Count > ConnectionsShown)
            lines.Add($"and {songs.Count - ConnectionsShown:N0} more");
        return string.Join("\n", lines);
    }

    private static string? JoinDetails(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return kept.Count == 0 ? null : string.Join(" · ", kept);
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} or the {names[1]}",
        _ => string.Join(", the ", names.Take(names.Count - 1)) + $" or the {names[^1]}",
    };

    private static string Failed(GeniusException ex) => $"Couldn't load the Genius page. {ex.Message.TrimEnd('.')}.";

    public void Dispose()
    {
        _showing.Cancel();
        _showing.Dispose();
    }
}

/// <summary>
/// A question and its answer, an explanation under the lyric line it explains
/// (<see cref="IsQuote"/>), or a comment under its author. <see cref="Figure"/>
/// is the right-hand note: "Verified", or a comment's date and votes.
/// </summary>
public sealed record GeniusEntry(string Heading, string Body, string? Figure, bool IsQuote);

/// <summary>A credit or connection line: the label, then who or what (several lines for connections).</summary>
public sealed record GeniusCreditRow(string Label, string Value);

/// <summary>One of the nearest results offered when nothing matched.</summary>
public sealed class GeniusChoice(GeniusHit hit)
{
    public GeniusHit Hit { get; } = hit;
    public string Label => Hit.Title;
    public string Detail => Hit.Artist;
}
