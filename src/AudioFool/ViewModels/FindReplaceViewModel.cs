using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.ViewModels;

/// <summary>One song's ticked tags, for a replace.</summary>
public sealed record ReplaceJob(Track Track, IReadOnlyList<ReplaceMatch> Tags);

/// <summary>One song a replace wrote, and what it wrote: what Undo puts back.</summary>
public sealed record UndoJob(Track Track, IReadOnlyList<ReplacedTag> Written);

/// <summary>
/// What a replace, or its undo, did across the songs: how many were written,
/// which were left alone because they no longer read as expected, which failed
/// and why, and what Undo would need.
/// </summary>
public sealed record ReplaceRun(int Saved, IReadOnlyList<string> LeftAlone,
                                IReadOnlyList<(string FileName, string Error)> Failed, IReadOnlyList<UndoJob> Undo)
{
    /// <summary>Names at most three files, then how many more.</summary>
    private const int NamesShown = 3;

    /// <summary>
    /// "Updated 132 songs.", or with the rest named: "Updated 130 of 132 songs.
    /// 1 had changed since Find and was left alone: a.flac. 1 couldn't be saved:
    /// b.flac (the file may be open in another program)."
    /// </summary>
    public string Describe(string done, int total, string since)
    {
        var text = LeftAlone.Count == 0 && Failed.Count == 0
            ? $"{done} {Songs(Saved)}."
            : $"{done} {Saved:N0} of {Songs(total)}.";

        if (LeftAlone.Count > 0)
            text += $" {LeftAlone.Count:N0} had changed {since} and "
                  + $"{(LeftAlone.Count == 1 ? "was" : "were")} left alone: {Names(LeftAlone)}.";

        if (Failed.Count > 0)
            text += $" {Failed.Count:N0} couldn't be saved: "
                  + $"{Names(Failed.Select(f => $"{f.FileName} ({f.Error})").ToList())}.";

        return text;
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= NamesShown
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(NamesShown))} and {names.Count - NamesShown:N0} more";

    public static string Songs(int count) => count == 1 ? "1 song" : $"{count:N0} songs";
}

/// <summary>A row of the preview: one tag of one song, and what the replacement makes of it.</summary>
public sealed partial class ReplaceRow : ObservableObject
{
    private readonly Action _ticked;

    public ReplaceRow(ReplaceMatch match, string file, Action ticked)
    {
        Match = match;
        File = file;
        _ticked = ticked;
        _after = match.Before;
    }

    public ReplaceMatch Match { get; }

    public string Field => FindReplace.Name(Match.Field);

    /// <summary>What the FIELD column sorts on: the tags' own order, Title first.</summary>
    public int FieldOrder => (int)Match.Field;

    public string Before => Match.Before;

    /// <summary>The song's path inside its music folder.</summary>
    public string File { get; }

    [ObservableProperty]
    private string _after;

    [ObservableProperty]
    private bool _isTicked = true;

    /// <summary>
    /// False while the replacement leaves the tag reading as it does ("rush" made
    /// "Rush" on a tag that already says so): such a row is out of view, out of the
    /// counts, and not written.
    /// </summary>
    public bool Changes => !string.Equals(After, Before, StringComparison.Ordinal);

    partial void OnIsTickedChanged(bool value) => _ticked();
}

/// <summary>
/// The Find and Replace window (session 63, the user's design).
/// <list type="bullet">
/// <item><b>Find</b> lists every tag of the songs it is given that holds the text,
/// one row per song and tag, all ticked.</item>
/// <item><b>AFTER</b> follows the Replace box as it is typed. Changing the Find text
/// clears the rows, so Replace only ever writes what the window shows; changing
/// an option finds again.</item>
/// <item><b>Replace</b> asks first (<see cref="ConfirmReplace"/>), then writes the
/// ticked rows song by song, with progress and then the result under the grid,
/// and clears the rows.</item>
/// <item><b>Undo</b> puts back the last replace, until the window closes.</item>
/// </list>
/// The writes themselves are the main view model's, so the library, library.json
/// and closing's wait for saves all see them.
/// </summary>
public sealed partial class FindReplaceViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<Track>> _songs;
    private readonly IReadOnlyList<string> _musicFolders;
    private readonly Func<IReadOnlyList<ReplaceJob>, TextReplace, IProgress<int>, Task<ReplaceRun>> _replace;
    private readonly Func<IReadOnlyList<UndoJob>, IProgress<int>, Task<ReplaceRun>> _undo;

    /// <summary>What the rows were found with, the replacement kept up to date; null when no rows are shown.</summary>
    private TextReplace? _found;

    private IReadOnlyList<UndoJob> _undoJobs = [];
    private bool _bulkTicking;

    /// <param name="songs">The songs Find looks through, asked for at each Find.</param>
    /// <param name="picked">How many songs were selected, when Find looks only through those; null for the library.</param>
    /// <param name="musicFolders">For the FILE column: paths are shown inside their music folder.</param>
    public FindReplaceViewModel(Func<IReadOnlyList<Track>> songs, int? picked, IReadOnlyList<string> musicFolders,
                                Func<IReadOnlyList<ReplaceJob>, TextReplace, IProgress<int>, Task<ReplaceRun>> replace,
                                Func<IReadOnlyList<UndoJob>, IProgress<int>, Task<ReplaceRun>> undo)
    {
        _songs = songs;
        WindowTitle = picked is { } count ? $"Find and Replace · {count:N0} tracks" : "Find and Replace";
        _musicFolders = musicFolders;
        _replace = replace;
        _undo = undo;
    }

    /// <summary>
    /// Asks before a replace, with the counts; true to go ahead. Settable so
    /// ThemeLab can answer without opening the prompt.
    /// </summary>
    /// <summary>"Find and Replace", or "Find and Replace · 12 tracks" for a selection, as the tag windows name theirs.</summary>
    public string WindowTitle { get; }

    public static Func<string, bool> ConfirmReplace { get; set; } = message =>
        Application.Current.Windows.OfType<FindReplaceWindow>().FirstOrDefault() is { } owner
        && PromptWindow.Confirm(owner, "Find and Replace", message, "Replace");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    private string _findText = "";

    [ObservableProperty]
    private string _replaceText = "";

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _wholeWords;

    /// <summary>Every match of the last Find; the window's view of it leaves out the rows that wouldn't change.</summary>
    [ObservableProperty]
    private IReadOnlyList<ReplaceRow> _rows = [];

    /// <summary>Under the grid: what Replace would change, then its progress, then what it did.</summary>
    [ObservableProperty]
    private string _footerText = "";

    /// <summary>The footer names a failure: semibold, as the other dialogs' error lines are.</summary>
    [ObservableProperty]
    private bool _footerIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(ReplaceCommand), nameof(UndoCommand))]
    private bool _isBusy;

    /// <summary>A replace has been made and not undone; the Undo button shows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    /// <summary>There are rows that would change, and every one is ticked: the header's tick box.</summary>
    [ObservableProperty]
    private bool _allTicked;

    /// <summary>Songs and tags the ticked rows would change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand))]
    private (int Songs, int Tags) _counts;

    /// <summary>"Update 132 songs? "(Remastered)" will be deleted from 140 tags."</summary>
    public string ConfirmationText
    {
        get
        {
            var (songs, tags) = Counts;
            var what = ReplaceText.Length == 0
                ? $"\"{FindText}\" will be deleted from {Tags(tags)}."
                : $"\"{FindText}\" will be replaced with \"{ReplaceText}\" in {Tags(tags)}.";
            return $"Update {ReplaceRun.Songs(songs)}? {what}";
        }
    }

    partial void OnFindTextChanged(string value) => ClearRows();

    partial void OnReplaceTextChanged(string value)
    {
        if (_found is null)
            return;

        _found = _found with { Replace = value };
        var shownChanged = false;
        foreach (var row in Rows)
        {
            var changed = row.Changes;
            row.After = _found.Apply(row.Before) ?? row.Before;
            shownChanged |= changed != row.Changes;
        }

        if (shownChanged)
            CollectionViewSource.GetDefaultView(Rows).Refresh();
        Recount();
    }

    partial void OnMatchCaseChanged(bool value) => FindAgain();

    partial void OnWholeWordsChanged(bool value) => FindAgain();

    /// <summary>An option refines what is shown: found again with it, ticks and all from scratch.</summary>
    private void FindAgain()
    {
        if (_found is not null)
            Find();
    }

    partial void OnRowsChanged(IReadOnlyList<ReplaceRow> value) =>
        CollectionViewSource.GetDefaultView(value).Filter = row => ((ReplaceRow)row).Changes;

    private bool CanFind() => !IsBusy && FindText.Length > 0;

    /// <summary>Every match, sorted by file and then tag, all ticked.</summary>
    [RelayCommand(CanExecute = nameof(CanFind))]
    private void Find()
    {
        var replace = new TextReplace(FindText, ReplaceText, MatchCase, WholeWords);
        var rows = FindReplace.Find(_songs(), replace)
            .Select(match => new ReplaceRow(match, FileText(match.Track.FilePath), OnRowTicked))
            .OrderBy(row => row.File, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.FieldOrder)
            .ToList();
        foreach (var row in rows)
            row.After = replace.Apply(row.Before) ?? row.Before;

        _found = replace;
        Rows = rows;
        FooterIsError = false;
        Recount();
    }

    private bool CanReplace() => !IsBusy && Counts.Tags > 0;

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task Replace()
    {
        if (ConfirmReplace(ConfirmationText))
            await ReplaceConfirmedAsync();
    }

    /// <summary>The replace, once confirmed: the ticked rows that change, grouped by song.</summary>
    public async Task ReplaceConfirmedAsync()
    {
        if (_found is not { } replace || !CanReplace())
            return;

        var jobs = Rows.Where(r => r.IsTicked && r.Changes)
            .GroupBy(r => r.Match.Track.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(song => new ReplaceJob(song.First().Match.Track, [.. song.Select(r => r.Match)]))
            .ToList();

        var run = await RunAsync(jobs.Count, "Saving", progress => _replace(jobs, replace, progress));
        FooterText = run.Describe("Updated", jobs.Count, "since Find");
        FooterIsError = run.Failed.Count > 0;
        _undoJobs = run.Undo;
        CanUndo = run.Undo.Count > 0;
    }

    private bool CanUndoNow() => !IsBusy && CanUndo;

    /// <summary>Puts back the last replace. No question asked: Replace can make it again.</summary>
    [RelayCommand(CanExecute = nameof(CanUndoNow))]
    private async Task Undo()
    {
        var jobs = _undoJobs;
        var run = await RunAsync(jobs.Count, "Putting back", progress => _undo(jobs, progress));
        FooterText = run.Describe("Put back", jobs.Count, "since the replace");
        FooterIsError = run.Failed.Count > 0;
        _undoJobs = [];
        CanUndo = false;
    }

    /// <summary>
    /// Runs a replace or an undo with its progress under the grid, then clears the
    /// rows: they no longer read as the files do.
    /// </summary>
    private async Task<ReplaceRun> RunAsync(int songs, string doing, Func<IProgress<int>, Task<ReplaceRun>> run)
    {
        IsBusy = true;
        FooterIsError = false;
        FooterText = $"{doing} 0 of {ReplaceRun.Songs(songs)}…";
        var progress = new Progress<int>(done => FooterText = $"{doing} {done:N0} of {ReplaceRun.Songs(songs)}…");
        try
        {
            return await run(progress);
        }
        finally
        {
            ClearRows();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Ticks or unticks every row that would change, from the header's tick box.
    /// Its click has already flipped the box, so the state is said again even when
    /// nothing changed (an empty table), to put the box back.
    /// </summary>
    [RelayCommand]
    private void ToggleAll()
    {
        SetTicked(Rows.Where(r => r.Changes), !AllTicked);
        OnPropertyChanged(nameof(AllTicked));
    }

    /// <summary>Ticks or unticks several rows at once (Space on a selection), counting once.</summary>
    public void SetTicked(IEnumerable<ReplaceRow> rows, bool ticked)
    {
        _bulkTicking = true;
        try
        {
            foreach (var row in rows)
                row.IsTicked = ticked;
        }
        finally
        {
            _bulkTicking = false;
        }

        Recount();
    }

    private void OnRowTicked()
    {
        if (!_bulkTicking)
            Recount();
    }

    private void Recount()
    {
        var tags = 0;
        var unticked = 0;
        var songs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (!row.Changes)
                continue;
            if (!row.IsTicked)
            {
                unticked++;
                continue;
            }

            tags++;
            songs.Add(row.Match.Track.FilePath);
        }

        Counts = (songs.Count, tags);
        AllTicked = tags > 0 && unticked == 0;
        FooterText = _found is null ? ""
            : Rows.Count == 0 ? "No matches."
            : $"{ReplaceRun.Songs(songs.Count)} · {Tags(tags)}";
    }

    private void ClearRows()
    {
        _found = null;
        Rows = new List<ReplaceRow>();
        Counts = (0, 0);
        AllTicked = false;
        FooterText = "";
        FooterIsError = false;
    }

    /// <summary>The path inside its music folder: "Rush\Moving Pictures\01 Tom Sawyer.flac".</summary>
    private string FileText(string path)
    {
        foreach (var folder in _musicFolders)
        {
            var root = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return path[root.Length..];
        }

        return path;
    }

    private static string Tags(int count) => count == 1 ? "1 tag" : $"{count:N0} tags";
}
