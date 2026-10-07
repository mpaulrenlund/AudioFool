using System.Globalization;
using System.Windows.Data;
using AudioFool.Core.Models;

namespace AudioFool.Formatting;

/// <summary>Whether the row's track (values[0]) is in the liked paths (values[1]).</summary>
internal sealed class IsLikedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is Track track && values[1] is IReadOnlySet<string> liked
        && liked.Contains(track.FilePath);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The # column's text: the song's place in the playlist while one is shown
/// (values[1] maps path to place), otherwise its track number. A song unliked
/// while Liked is shown has no place any more, and shows none.
/// </summary>
internal sealed class TrackNumberOrPositionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 1 || values[0] is not Track track)
            return "";

        if (values.Length > 1 && values[1] is IReadOnlyDictionary<string, int> positions)
            return positions.TryGetValue(track.FilePath, out var place) ? Display.Number(place) : "";

        return Display.Number(track.TrackNumber);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
