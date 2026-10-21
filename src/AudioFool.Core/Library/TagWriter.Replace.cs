using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>A tag a replace changed: every value it held before and holds now, so Undo can put it back.</summary>
public sealed record ReplacedTag(ReplaceField Field, IReadOnlyList<string> Before, IReadOnlyList<string> After);

/// <summary>What a replace, or its undo, did to one song.</summary>
public enum ReplaceOutcome
{
    /// <summary>Written.</summary>
    Saved,

    /// <summary>
    /// Left exactly as it is, not even saved unchanged: the file no longer reads as
    /// the preview showed (edited since, in another tagger say), or for an undo, as
    /// the replace left it.
    /// </summary>
    Changed,

    /// <summary>The save failed, and the file is as it was; the message says why.</summary>
    Failed,
}

/// <summary>One song's replace or undo. Never thrown across the boundary, as <see cref="TagWriteResult"/>.</summary>
public sealed record ReplaceWriteResult
{
    public required ReplaceOutcome Outcome { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The library's copy, reading as the file now does. Set when saved.</summary>
    public Track? UpdatedTrack { get; init; }

    /// <summary>The tags written, each with its values before and after: what Undo puts back.</summary>
    public IReadOnlyList<ReplacedTag> Written { get; init; } = [];
}

public static partial class TagWriter
{
    /// <summary>
    /// One song's share of a Find and Replace (session 63): each tag in
    /// <paramref name="tags"/> gets the replace, worked out again from what the file
    /// holds as it is saved, through the same safe save as every tag edit.
    /// <list type="bullet">
    /// <item>A song is written whole or not at all. If any of its tags no longer reads
    /// as the preview showed (<see cref="ReplaceMatch.Before"/>), it is left exactly as
    /// it is (<see cref="ReplaceOutcome.Changed"/>): the save's rehearsal stops before
    /// anything is written.</item>
    /// <item>Every artist the file holds gets the replace. The library keeps only the
    /// first, so a song by "A; B" written from it would lose B.</item>
    /// <item>A song with no artist of its own shows its album artist as the artist, so
    /// both tags are found. With Album Artist written too, no artist tag is added; on
    /// its own, Artist is written as an artist tag of the song's own.</item>
    /// <item>A value the replace empties is removed, as a cleared field is.</item>
    /// </list>
    /// </summary>
    /// <param name="track">The library's copy of the song, which the updated copy is made from.</param>
    public static ReplaceWriteResult WriteReplacement(Track track, IReadOnlyList<ReplaceMatch> tags, TextReplace replace,
                                                      IFileHolder? holder = null) =>
        Rewrite(track, tag => PlanReplacement(tag, tags, replace), holder);

    /// <summary>
    /// Puts back what <see cref="WriteReplacement"/> wrote, value for value. A song
    /// whose tags no longer read as the replace left them is left alone
    /// (<see cref="ReplaceOutcome.Changed"/>).
    /// </summary>
    public static ReplaceWriteResult UndoReplacement(Track track, IReadOnlyList<ReplacedTag> written,
                                                     IFileHolder? holder = null) =>
        Rewrite(track, tag => PlanUndo(tag, written), holder);

    /// <summary>
    /// Saves what <paramref name="plan"/> works out from the file's tags, or nothing
    /// when it says the file no longer reads as expected (null). The plan is made
    /// inside the save, against the file as the save reads it, so a change made a
    /// moment before is seen; throwing there stops the rehearsal before any write.
    /// </summary>
    private static ReplaceWriteResult Rewrite(Track track, Func<TagLib.Tag, List<ReplacedTag>?> plan, IFileHolder? holder)
    {
        List<ReplacedTag> written = [];
        var noLongerReads = false;
        var reads = (Title: "", Artist: "", AlbumArtist: "", Album: "");

        var save = SaveTags(track.FilePath, file =>
        {
            var tag = file.Tag;
            if (plan(tag) is not { } changes)
            {
                noLongerReads = true;
                throw new NoLongerReadsException();
            }

            foreach (var change in changes)
                SetValues(tag, change.Field, change.After);

            written = changes;

            // As TagReader reads them, so the library's copy matches the file.
            reads = (Trimmed(tag.Title), Trimmed(tag.FirstPerformer ?? tag.FirstAlbumArtist),
                     Trimmed(tag.FirstAlbumArtist), Trimmed(tag.Album));
        }, art: null, holder);

        if (noLongerReads)
            return new ReplaceWriteResult { Outcome = ReplaceOutcome.Changed };

        if (!save.Success)
            return new ReplaceWriteResult { Outcome = ReplaceOutcome.Failed, ErrorMessage = save.ErrorMessage };

        var edit = new TracksTagEdit { Title = reads.Title, Artist = reads.Artist, AlbumArtist = reads.AlbumArtist, Album = reads.Album };
        return new ReplaceWriteResult
        {
            Outcome = ReplaceOutcome.Saved,
            UpdatedTrack = track.WithSelectedTags(edit, FileStamp.For(track.FilePath)),
            Written = written,
        };
    }

    /// <summary>Thrown inside a save to stop it: the file no longer reads as it did.</summary>
    private sealed class NoLongerReadsException : Exception;

    /// <summary>The tags to write, from the file's own values; null when one no longer reads as the preview showed.</summary>
    private static List<ReplacedTag>? PlanReplacement(TagLib.Tag tag, IReadOnlyList<ReplaceMatch> tags, TextReplace replace)
    {
        var plan = new List<ReplacedTag>();
        var writesAlbumArtist = tags.Any(t => t.Field == ReplaceField.AlbumArtist);

        foreach (var match in tags)
        {
            var current = Values(tag, match.Field);

            if (match.Field == ReplaceField.Artist && current.Count == 0)
            {
                // Shown as the artist is the album artist, which this file has instead.
                var shown = Values(tag, ReplaceField.AlbumArtist);
                if (Trimmed(shown.FirstOrDefault()) != match.Before)
                    return null;
                if (writesAlbumArtist)
                    continue;
                if (Replaced(shown, replace) is not { } given)
                    return null;
                plan.Add(new ReplacedTag(ReplaceField.Artist, [], given));
                continue;
            }

            if (Trimmed(current.FirstOrDefault()) != match.Before || Replaced(current, replace) is not { } after)
                return null;
            plan.Add(new ReplacedTag(match.Field, current, after));
        }

        return plan;
    }

    /// <summary>The tags to put back; null when one no longer reads as the replace left it.</summary>
    private static List<ReplacedTag>? PlanUndo(TagLib.Tag tag, IReadOnlyList<ReplacedTag> written)
    {
        var plan = new List<ReplacedTag>();
        foreach (var change in written)
        {
            var current = Values(tag, change.Field);
            if (!current.SequenceEqual(change.After, StringComparer.Ordinal))
                return null;
            plan.Add(new ReplacedTag(change.Field, current, change.Before));
        }

        return plan;
    }

    /// <summary>Each value with the replace made; a value it empties goes. Null when it changed none.</summary>
    private static List<string>? Replaced(IReadOnlyList<string> values, TextReplace replace)
    {
        var changed = false;
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (replace.Apply(value) is not { } after)
            {
                result.Add(value);
                continue;
            }

            changed = true;
            if (after.Length > 0)
                result.Add(after);
        }

        return changed ? result : null;
    }

    /// <summary>A tag's values as the file holds them, untrimmed; none for a tag it lacks.</summary>
    private static IReadOnlyList<string> Values(TagLib.Tag tag, ReplaceField field) => field switch
    {
        ReplaceField.Title => One(tag.Title),
        ReplaceField.Artist => tag.Performers ?? [],
        ReplaceField.AlbumArtist => tag.AlbumArtists ?? [],
        _ => One(tag.Album),
    };

    private static IReadOnlyList<string> One(string? value) => string.IsNullOrEmpty(value) ? [] : [value];

    /// <summary>Sets a tag's values; none removes it, as a cleared field does.</summary>
    private static void SetValues(TagLib.Tag tag, ReplaceField field, IReadOnlyList<string> values)
    {
        switch (field)
        {
            case ReplaceField.Title:
                tag.Title = values.Count == 0 ? null : values[0];
                break;
            case ReplaceField.Artist:
                tag.Performers = [.. values];
                break;
            case ReplaceField.AlbumArtist:
                tag.AlbumArtists = [.. values];
                break;
            default:
                tag.Album = values.Count == 0 ? null : values[0];
                break;
        }
    }

    private static string Trimmed(string? value) => value?.Trim() ?? "";
}
