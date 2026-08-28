namespace AudioFool.Core.Playback;

/// <summary>
/// The order tracks are played in, kept separate from the queue itself.
/// <para>
/// The queue always stays in album order. This holds a permutation of its
/// indices - <c>_order[p]</c> is the queue index of the p-th track to play -
/// so turning shuffle off restores the album running order without disturbing
/// what is playing, and Previous retraces the order actually heard rather than
/// the order the tracks happen to sit in.
/// </para>
/// <para>
/// Pure logic, deliberately free of any BASS dependency, so it can be tested
/// without a sound device.
/// </para>
/// </summary>
public sealed class PlayOrder
{
    private int[] _order = [];

    /// <summary>Inverse of <see cref="_order"/>: queue index to play position.</summary>
    private int[] _positionOf = [];

    public int Count => _order.Length;

    public bool IsShuffled { get; private set; }

    /// <summary>The permutation itself. Exposed for tests and diagnostics.</summary>
    public IReadOnlyList<int> Order => _order;

    /// <summary>
    /// Rebuilds the order for a queue of <paramref name="count"/> tracks.
    /// <para>
    /// When shuffling, the track at <paramref name="currentIndex"/> is pulled to
    /// the front. Toggling shuffle on must not change what is playing right now -
    /// only what comes after it.
    /// </para>
    /// </summary>
    public void Reset(int count, int currentIndex, bool shuffle, Random? rng = null)
    {
        if (count <= 0)
        {
            _order = [];
            _positionOf = [];
            IsShuffled = false;
            return;
        }

        var order = new int[count];
        for (var i = 0; i < count; i++)
            order[i] = i;

        if (shuffle)
        {
            rng ??= Random.Shared;

            // Fisher-Yates.
            for (var i = count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            if (currentIndex >= 0 && currentIndex < count)
            {
                var landedAt = Array.IndexOf(order, currentIndex);
                (order[0], order[landedAt]) = (order[landedAt], order[0]);
            }
        }

        var positionOf = new int[count];
        for (var p = 0; p < count; p++)
            positionOf[order[p]] = p;

        _order = order;
        _positionOf = positionOf;
        IsShuffled = shuffle;
    }

    /// <summary>Where a queue index sits in the play order, or -1 if out of range.</summary>
    public int PositionOf(int index) =>
        index >= 0 && index < _positionOf.Length ? _positionOf[index] : -1;

    /// <summary>
    /// The queue index that follows <paramref name="fromIndex"/>, or -1 when the
    /// order runs out and <paramref name="wrap"/> is false.
    /// </summary>
    public int Next(int fromIndex, bool wrap) => Step(fromIndex, 1, wrap);

    /// <summary>
    /// The queue index before <paramref name="fromIndex"/>, or -1 at the start of
    /// the order when <paramref name="wrap"/> is false.
    /// </summary>
    public int Previous(int fromIndex, bool wrap) => Step(fromIndex, -1, wrap);

    private int Step(int fromIndex, int delta, bool wrap)
    {
        var count = _order.Length;
        if (count == 0)
            return -1;

        var position = PositionOf(fromIndex);
        if (position < 0)
            return -1;

        var target = position + delta;
        if (target < 0 || target >= count)
        {
            if (!wrap)
                return -1;

            target = (target + count) % count;
        }

        return _order[target];
    }
}
