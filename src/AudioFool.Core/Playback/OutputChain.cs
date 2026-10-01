using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;

namespace AudioFool.Core.Playback;

/// <summary>
/// One BASSmix mixer bound to one WASAPI device connection, running at a fixed
/// sample rate.
/// <para>
/// BASS is initialised on the "no sound" device (see <see cref="BassRuntime"/>) and
/// used purely as a decoder. Everything is mixed into a <em>decode</em> channel that
/// WASAPI pulls from through <see cref="WasapiProcedure"/>. That indirection is what
/// makes exclusive mode possible: BASS never opens the endpoint itself, so it can't
/// compete with us for it.
/// </para>
/// <para>
/// In <see cref="OutputMode.Exclusive"/> the chain is opened at the source material's
/// own rate, so nothing is resampled. A rate change therefore means a new chain -
/// that is the one place a gap between tracks is unavoidable, and it is a physical
/// constraint of re-clocking the DAC rather than a shortcut.
/// </para>
/// </summary>
internal sealed class OutputChain : IDisposable
{
    /// <summary>
    /// Device buffer. Long enough to survive a scheduling hiccup, short enough that
    /// the seek bar isn't visibly ahead of what you're hearing.
    /// </summary>
    private const float BufferSeconds = 0.2f;

    /// <summary>
    /// The fade either side of a seek. A seek joins two unrelated points of the
    /// waveform, and that step is an audible click; measured on quiet orchestral
    /// tracks, up to 45 times the track's own sharpest sample-to-sample change.
    /// 5 ms out and 5 ms in brings it under a tenth. BASSmix's own ramp-in only
    /// halves it, since the old audio still stops mid-wave.
    /// </summary>
    private const double SeekFadeSeconds = 0.005;

    private sealed record PendingSeek(int Stream, long Bytes, bool Fade);

    // Held in a field for the lifetime of the chain: BASS calls this from its own
    // thread, and a collected delegate is an instant crash.
    private readonly WasapiProcedure _wasapiProc;

    private readonly int _mixerChannels;
    private readonly int _fadeFrames;
    private readonly float[] _fadeScratch;
    private PendingSeek? _pendingSeek;
    private bool _fadeInNext;

    // The volume asked for, and the one the last block ended on. Fill glides from
    // one to the other across a block, so a drag or Mute doesn't click.
    private float _gain = 1f;
    private float _appliedGain = 1f;
    private float[] _gainScratch = [];

    private int _mixer;
    private bool _disposed;

    private OutputChain(int mixer, int sampleRate, int channels, OutputMode mode)
    {
        _mixer = mixer;
        _wasapiProc = Fill;
        _mixerChannels = channels;
        _fadeFrames = Math.Max(1, (int)(sampleRate * SeekFadeSeconds));
        _fadeScratch = new float[_fadeFrames * channels];
        SampleRate = sampleRate;
        Channels = channels;
        Mode = mode;
    }

    public int Mixer => _mixer;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public OutputMode Mode { get; }

    /// <summary>
    /// Moves <paramref name="stream"/> to <paramref name="bytes"/> between two of
    /// the device's pulls, fading the audio out before the jump and in after it
    /// when <paramref name="fade"/> is set. A later call replaces one not yet
    /// applied. A stopped or paused device applies it on its next pull.
    /// </summary>
    public void SeekBetweenBlocks(int stream, long bytes, bool fade) =>
        Volatile.Write(ref _pendingSeek, new PendingSeek(stream, bytes, fade));

    /// <summary>The position a seek not yet applied will move <paramref name="stream"/> to.</summary>
    public long? PendingSeekBytes(int stream) =>
        Volatile.Read(ref _pendingSeek) is { } seek && seek.Stream == stream ? seek.Bytes : null;

    /// <summary>
    /// The device's pull: drains the mixer, and applies a pending seek at the end
    /// of the block, so the block's tail and the next block's head can be faded.
    /// The mixer is float, laid out as it was created, whatever the device takes.
    /// </summary>
    private int Fill(IntPtr buffer, int length, IntPtr user)
    {
        var got = Bass.ChannelGetData(_mixer, buffer, length);
        if (got <= 0)
            return got;

        var frames = got / sizeof(float) / _mixerChannels;
        var fadeFrames = Math.Min(_fadeFrames, frames / 2);

        if (_fadeInNext)
        {
            _fadeInNext = false;
            Fade(buffer, 0, fadeFrames, rising: true);
        }

        if (Interlocked.Exchange(ref _pendingSeek, null) is { } seek)
        {
            if (seek.Fade)
                Fade(buffer, frames - fadeFrames, fadeFrames, rising: false);
            BassMix.ChannelSetPosition(seek.Stream, seek.Bytes);
            _fadeInNext = seek.Fade;
        }

        ApplyGain(buffer, frames);
        return got;
    }

    private void ApplyGain(IntPtr buffer, int frames)
    {
        var from = _appliedGain;
        var to = Volatile.Read(ref _gain);
        if (from == 1f && to == 1f)
            return;

        var samples = frames * _mixerChannels;
        if (_gainScratch.Length < samples)
            _gainScratch = new float[samples];

        Marshal.Copy(buffer, _gainScratch, 0, samples);
        for (var f = 0; f < frames; f++)
        {
            var gain = from + (to - from) * (f + 1) / frames;
            for (var c = 0; c < _mixerChannels; c++)
                _gainScratch[f * _mixerChannels + c] *= gain;
        }
        Marshal.Copy(_gainScratch, 0, buffer, samples);
        _appliedGain = to;
    }

    /// <summary>A raised-cosine gain over <paramref name="count"/> frames from <paramref name="firstFrame"/>.</summary>
    private void Fade(IntPtr buffer, int firstFrame, int count, bool rising)
    {
        if (count <= 0)
            return;

        var at = buffer + firstFrame * _mixerChannels * sizeof(float);
        var samples = count * _mixerChannels;
        Marshal.Copy(at, _fadeScratch, 0, samples);
        for (var f = 0; f < count; f++)
        {
            var t = rising ? (double)f / count : (double)(f + 1) / count;
            var gain = (float)(rising ? 0.5 - 0.5 * Math.Cos(Math.PI * t) : 0.5 + 0.5 * Math.Cos(Math.PI * t));
            for (var c = 0; c < _mixerChannels; c++)
                _fadeScratch[f * _mixerChannels + c] *= gain;
        }
        Marshal.Copy(_fadeScratch, 0, at, samples);
    }

    /// <summary>The bit depth WASAPI actually opened, for display. 0 when unknown.</summary>
    public int DeviceBitDepth { get; private set; }

    /// <summary>
    /// False in exclusive mode: attenuating in software would defeat the entire
    /// point of a bit-perfect path, so the volume control is refused rather than
    /// silently making the output not bit-perfect.
    /// </summary>
    public bool SupportsVolume => Mode == OutputMode.Shared;

    /// <summary>
    /// Opens a chain, or returns null with the reason. The caller decides whether
    /// to retry in a different mode.
    /// </summary>
    public static OutputChain? TryCreate(OutputMode mode, int sampleRate, int channels, out string? error)
    {
        error = null;

        var flags = BassFlags.MixerNonStop | BassFlags.Float | BassFlags.Decode;
        var mixer = BassMix.CreateMixerStream(sampleRate, channels, flags);
        if (mixer == 0)
        {
            error = $"Could not create a {sampleRate} Hz mixer ({Bass.LastError}).";
            return null;
        }

        // The callback (Fill) drains the mixer. Returning less than requested would
        // stall the device, which MixerNonStop prevents by emitting silence when no
        // source is attached.
        var chain = new OutputChain(mixer, sampleRate, channels, mode);

        var initFlags = mode == OutputMode.Exclusive
            ? WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven | WasapiInitFlags.Buffer
            : WasapiInitFlags.Shared | WasapiInitFlags.Buffer;

        if (!BassWasapi.Init(-1, sampleRate, channels, initFlags, BufferSeconds, 0f, chain._wasapiProc))
        {
            error = mode == OutputMode.Exclusive
                ? $"The device would not open in exclusive mode at {sampleRate} Hz ({Bass.LastError})."
                : $"WASAPI would not open at {sampleRate} Hz ({Bass.LastError}).";

            Bass.StreamFree(mixer);
            return null;
        }

        var depth = 0;
        if (BassWasapi.GetInfo(out var info))
        {
            depth = info.Format switch
            {
                WasapiFormat.Float => 32,
                WasapiFormat.Bit32 => 32,
                WasapiFormat.Bit24 => 24,
                WasapiFormat.Bit16 => 16,
                WasapiFormat.Bit8 => 8,
                _ => 0,
            };

            // The device can legitimately land on a different rate than requested
            // in shared mode; report what we actually got.
            if (info.Frequency > 0)
                chain.SampleRate = info.Frequency;
            if (info.Channels > 0)
                chain.Channels = info.Channels;
        }

        chain.DeviceBitDepth = depth;
        return chain;
    }

    public bool Start() => BassWasapi.Start();

    /// <summary>Stops the device without discarding what's buffered.</summary>
    public void Pause() => BassWasapi.Stop(false);

    public void Resume() => BassWasapi.Start();

    /// <summary>Stops the device and discards what's buffered.</summary>
    public void Stop() => BassWasapi.Stop(true);

    /// <summary>
    /// Applied by <see cref="Fill"/>. BASS's volume attribute is no use here: it
    /// is ignored when a decode channel is read directly, which is all this mixer
    /// ever is (measured: the same output level at 1, 0.5, 0.1 and 0).
    /// Exclusive mode is pinned to unity gain; see SupportsVolume.
    /// </summary>
    public void SetVolume(double volume) =>
        Volatile.Write(ref _gain, SupportsVolume ? (float)Math.Clamp(volume, 0, 1) : 1f);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        BassWasapi.Stop(true);
        BassWasapi.Free();

        if (_mixer != 0)
        {
            Bass.StreamFree(_mixer);
            _mixer = 0;
        }
    }
}
