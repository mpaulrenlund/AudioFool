namespace AudioFool.Core.Library;

/// <summary>
/// Files Windows hands AudioFool to open: a double-click on a song in Explorer, or
/// Open with. Explorer starts one copy of the app per file, and the copy already
/// running collects them (the app's SingleInstance), so a batch arrives in no
/// particular order.
/// </summary>
public static class OpenedFiles
{
    /// <summary>
    /// The music among <paramref name="args"/>: existing files of a kind AudioFool
    /// plays, as full paths, each once, in the order given.
    /// </summary>
    public static IReadOnlyList<string> Playable(IEnumerable<string> args, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            string path;
            try
            {
                path = Path.GetFullPath(arg.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (AudioFormats.IsSupported(path) && exists(path) && seen.Add(path))
                result.Add(path);
        }

        return result;
    }

    /// <summary>
    /// The order to play a batch in: by folder, then by file name as Explorer sorts
    /// names, numbers by value ("2 Song" before "10 Song"), so files picked from
    /// one folder play as they were listed there.
    /// </summary>
    public static IReadOnlyList<string> InPlayOrder(IEnumerable<string> paths) =>
        paths
            .OrderBy(p => Path.GetDirectoryName(p) ?? "", NaturalComparer.Instance)
            .ThenBy(p => Path.GetFileName(p), NaturalComparer.Instance)
            .ToList();

    /// <summary>Whether <paramref name="path"/> is in one of the music folders, so the library may hold it.</summary>
    public static bool InMusicFolders(string path, IEnumerable<string> folders) =>
        folders.Any(folder => LibraryRelocator.IsUnder(path, folder));

    /// <summary>Text with runs of digits compared by value, ignoring case.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var startX = i;
                    var startY = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                    // By value: shorter without leading zeros is smaller, then digit by digit.
                    var digitsX = x.AsSpan(startX, i - startX).TrimStart('0');
                    var digitsY = y.AsSpan(startY, j - startY).TrimStart('0');
                    if (digitsX.Length != digitsY.Length)
                        return digitsX.Length.CompareTo(digitsY.Length);

                    var byDigits = digitsX.SequenceCompareTo(digitsY);
                    if (byDigits != 0)
                        return byDigits;

                    continue;
                }

                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0)
                    return byChar;

                i++;
                j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
