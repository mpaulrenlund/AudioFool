using AudioFool.Core.Models;

namespace AudioFool.Core.Library;

/// <summary>The track-grid columns that can be edited in place.</summary>
public enum InlineField
{
    TrackNumber,
    Title,
    Artist,
    Album,
}

/// <summary>
/// What editing one cell of the track grid comes to: an edit to write, nothing
/// (the text is what the track already has), or a reason it cannot be saved.
/// </summary>
public readonly record struct InlineEditResult(TracksTagEdit? Edit, string? Error)
{
    public static readonly InlineEditResult Unchanged = new(null, null);
}

/// <summary>
/// Turns the text typed into one cell of the track grid into a
/// <see cref="TracksTagEdit"/> that sets that one field and nothing else.
/// <para>
/// The rules are the tag dialog's: text is trimmed, an empty Track # clears it,
/// and an album that was there cannot be emptied, since that would leave the
/// track filed under no album at all. Unchanged text writes nothing, so leaving
/// a cell without typing never touches the file.
/// </para>
/// </summary>
public static class InlineTagEdit
{
    /// <summary>The text a cell's edit box starts with - what the grid shows.</summary>
    public static string InitialText(Track track, InlineField field) => field switch
    {
        InlineField.TrackNumber => track.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        InlineField.Title => track.DisplayTitle,
        InlineField.Artist => track.Artist,
        InlineField.Album => track.Album,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static InlineEditResult Build(Track track, InlineField field, string text)
    {
        text = text.Trim();

        // A title-less track shows its file name, so a box left as it started
        // is not a request to write the file name in as the title.
        if (text == InitialText(track, field).Trim())
            return InlineEditResult.Unchanged;

        switch (field)
        {
            case InlineField.TrackNumber:
                if (text.Length == 0)
                    return new(new TracksTagEdit { TrackNumber = new NumberEdit(null) }, null);
                if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
                    return new(null, "Track # must be a whole number.");
                return number == track.TrackNumber
                    ? InlineEditResult.Unchanged
                    : new(new TracksTagEdit { TrackNumber = new NumberEdit(number) }, null);

            case InlineField.Title:
                return new(new TracksTagEdit { Title = text }, null);

            case InlineField.Artist:
                return new(new TracksTagEdit { Artist = text }, null);

            case InlineField.Album:
                if (text.Length == 0)
                    return new(null, "Album can't be emptied.");
                return new(new TracksTagEdit { Album = text }, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }
}
