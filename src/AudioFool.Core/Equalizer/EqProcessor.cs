namespace AudioFool.Core.Equalizer;

/// <summary>
/// Runs an <see cref="EqDesign"/> over interleaved float blocks, in place, keeping
/// each filter's memory from one block to the next so a block boundary (or a
/// gapless join, or a seek) is inaudible.
/// <para>
/// Made for the device's thread: <see cref="Process"/> allocates nothing, takes no
/// lock and logs nothing. The state is set aside here, once, for
/// <see cref="EqDesign.MaxBands"/> filters.
/// </para>
/// </summary>
public sealed class EqProcessor
{
    // Transposed direct form II: two doubles per filter per channel,
    // laid out [channel][filter][z1, z2].
    private readonly double[] _state;
    private EqDesign? _last;

    public EqProcessor(int channels)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));
        Channels = channels;
        _state = new double[channels * EqDesign.MaxBands * 2];
    }

    public int Channels { get; }

    /// <summary>
    /// Runs <paramref name="design"/> over <paramref name="samples"/> (whole frames of
    /// <see cref="Channels"/> interleaved samples) and returns how many came out past
    /// full scale. An identity design, or null, leaves every sample untouched.
    /// </summary>
    public int Process(EqDesign? design, Span<float> samples)
    {
        if (design is null || design.IsIdentity)
        {
            if (_last is not null)
            {
                Array.Clear(_state);
                _last = null;
            }
            return 0;
        }

        if (!ReferenceEquals(design, _last))
        {
            // A different set of filters: memory left by filters past the new count
            // would come back to life if a later design had more, so clear it. The
            // filters both share carry theirs over, which keeps a change of preset
            // from starting every filter from silence.
            var keep = Math.Min(design.FilterCount, _last?.FilterCount ?? 0);
            for (var c = 0; c < Channels; c++)
            {
                var from = (c * EqDesign.MaxBands + keep) * 2;
                var to = (c + 1) * EqDesign.MaxBands * 2;
                Array.Clear(_state, from, to - from);
            }
            _last = design;
        }

        var filters = design.Filters;
        var count = filters.Length;
        var preamp = design.PreampGain;
        var channels = Channels;
        var frames = samples.Length / channels;
        var clipped = 0;

        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                var i = f * channels + c;
                var x = samples[i] * preamp;
                var s = c * EqDesign.MaxBands * 2;
                for (var b = 0; b < count; b++, s += 2)
                {
                    ref readonly var q = ref filters[b];
                    var y = q.B0 * x + _state[s];
                    _state[s] = q.B1 * x - q.A1 * y + _state[s + 1];
                    _state[s + 1] = q.B2 * x - q.A2 * y;
                    x = y;
                }

                if (x > 1 || x < -1)
                    clipped++;
                samples[i] = (float)x;
            }
        }

        FlushDenormals(count);
        return clipped;
    }

    /// <summary>
    /// Filter memory decays towards zero in silence, and numbers that small are slow
    /// to work with on x86. Anything below -600 dB is nothing anyone can hear.
    /// </summary>
    private void FlushDenormals(int count)
    {
        for (var c = 0; c < Channels; c++)
        {
            var s = c * EqDesign.MaxBands * 2;
            for (var k = 0; k < count * 2; k++)
            {
                if (Math.Abs(_state[s + k]) < 1e-30)
                    _state[s + k] = 0;
            }
        }
    }

    /// <summary>Forgets every filter's memory, as for a stream that starts from nothing.</summary>
    public void Reset()
    {
        Array.Clear(_state);
        _last = null;
    }
}
