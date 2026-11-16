using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioFool.Core.Equalizer;
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

    /// <summary>How often exclusive mode tops the buffer up: shared mode's rhythm.</summary>
    private const float ExclusivePeriodSeconds = 0.01f;

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
    // one to the other across a block, so a drag or Mute doesn't click. None has
    // ended on anything before the first block, which starts at the volume asked
    // for: starting at 1 played a new chain's first block (up to the whole 200 ms
    // prefill) from full volume down to a low setting.
    private float _gain = 1f;
    private float _appliedGain = float.NaN;
    private float[] _gainScratch = [];

    // The equalizer (session 79). The engine hands over a design for MixerRate
    // (null for none), and says whether the playing stream is DoP, which the EQ
    // must never touch. Fill reads both once, before it pulls, so a block is
    // either equalized or not as a whole. The rest is the device thread's own:
    // the design last run, two processors (the one in use and one to crossfade
    // into), and the chunk the samples are copied into to run them.
    private const double EqFadeSeconds = 0.01;
    private const int EqChunkFrames = 2048;
    private EqDesign? _eqDesign;
    private volatile bool _eqSuspended;
    private volatile bool _eqResetNext;
    private EqDesign? _eqApplied;
    private EqProcessor _eqActive;
    private EqProcessor _eqSpare;
    private readonly int _eqFadeFrames;
    private readonly float[] _eqOld;
    private readonly float[] _eqNew;

    private int _mixer;
    private bool _disposed;

    private OutputChain(int mixer, int sampleRate, int channels, OutputMode mode)
    {
        _mixer = mixer;
        _wasapiProc = Fill;
        _mixerChannels = channels;
        MixerRate = sampleRate;
        _fadeFrames = Math.Max(1, (int)(sampleRate * SeekFadeSeconds));
        _fadeScratch = new float[_fadeFrames * channels];
        _eqActive = new EqProcessor(channels);
        _eqSpare = new EqProcessor(channels);
        _eqFadeFrames = Math.Max(1, (int)(sampleRate * EqFadeSeconds));
        var eqChunk = Math.Max(EqChunkFrames, _eqFadeFrames) * channels;
        _eqOld = new float[eqChunk];
        _eqNew = new float[eqChunk];
        SampleRate = sampleRate;
        Channels = channels;
        Mode = mode;
    }

    public int Mixer => _mixer;

    /// <summary>
    /// Runs on the device's thread just before each pull from the mixer, so the
    /// engine can note where the playing track stands (see AudioEngine.SpliceDelay).
    /// </summary>
    public Action? BeforePull { get; set; }

    /// <summary>
    /// Runs on the device's thread once a pull is in the device's buffer, before
    /// the volume is applied: the buffer (to read, never to change), its frame
    /// count, and whether a seek was made at its end (the next pull starts at the
    /// seek's target). The engine notes what the device now holds
    /// (<see cref="HearingTimeline"/>) and copies it for the visualizer
    /// (<see cref="SampleTap"/>).
    /// </summary>
    public Action<IntPtr, int, bool>? AfterPull { get; set; }

    /// <summary>The channels in the buffer: the mixer's, which the device was opened with.</summary>
    public int MixerChannels => _mixerChannels;

    /// <summary>
    /// The rate the mixer runs at, which every source is resampled to: the rate
    /// the device was opened with. <see cref="SampleRate"/> is what the device
    /// reports, which is the same unless shared mode landed elsewhere.
    /// </summary>
    public int MixerRate { get; }

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

    /// <summary>
    /// Fades the next block in, for a stream added mid-track to a stopped device.
    /// Call before <see cref="Start"/>.
    /// </summary>
    public void FadeInNextBlock() => Volatile.Write(ref _fadeInNext, true);

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
        BeforePull?.Invoke();

        // Decided before the pull: the END sync may switch tracks inside it, and
        // a block that began on DoP must stay untouched to its end.
        var eqSuspended = _eqSuspended;
        var eqTarget = eqSuspended ? null : Volatile.Read(ref _eqDesign);

        var timing = PlaybackDiagnostics.FillBegin();
        var got = Bass.ChannelGetData(_mixer, buffer, length);
        PlaybackDiagnostics.FillEnd(timing);
        if (got <= 0)
            return got;

        var frames = got / sizeof(float) / _mixerChannels;
        var fadeFrames = Math.Min(_fadeFrames, frames / 2);

        if (_fadeInNext)
        {
            _fadeInNext = false;
            Fade(buffer, 0, fadeFrames, rising: true);
        }

        var seek = Volatile.Read(ref _pendingSeek);
        if (seek is not null)
        {
            if (seek.Fade)
                Fade(buffer, frames - fadeFrames, fadeFrames, rising: false);
            BassMix.ChannelSetPosition(seek.Stream, seek.Bytes);
            _fadeInNext = seek.Fade;
        }

        ApplyEqualizer(buffer, frames, eqTarget, eqSuspended);
        AfterPull?.Invoke(buffer, frames, seek is not null);
        ApplyGain(buffer, frames);

        // Only now is the seek no longer pending: until AfterPull has noted it,
        // PendingSeekBytes is what keeps the seek bar on the target. A seek asked
        // for meanwhile stays pending for the next pull.
        if (seek is not null)
            Interlocked.CompareExchange(ref _pendingSeek, null, seek);
        return got;
    }

    /// <summary>
    /// The frames the device has been given and not yet played: what stands between
    /// the decoder and the speakers. -1 when the device can't say.
    /// </summary>
    public long QueuedFrames
    {
        get
        {
            // The buffer is float at the chain's layout (measured: 200 ms at 44.1 kHz
            // stereo is 70,560 bytes), whatever format the device itself takes.
            var bytes = BassWasapi.GetData(IntPtr.Zero, (int)DataFlags.Available);
            return bytes >= 0 ? bytes / (sizeof(float) * _mixerChannels) : -1;
        }
    }

    private void ApplyGain(IntPtr buffer, int frames)
    {
        var to = Volatile.Read(ref _gain);
        var from = float.IsNaN(_appliedGain) ? to : _appliedGain;
        _appliedGain = to;
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
    }

    /// <summary>
    /// Runs the equalizer over the block, in place. With none asked for and none
    /// running, the samples are not touched at all (Bit-Perfect stays bit-perfect).
    /// A change (on, off, another preset) crossfades from the old output to the
    /// new over <see cref="EqFadeSeconds"/>, as the volume glides, so it doesn't
    /// click; the new filters start from silence. A DoP block is never touched.
    /// </summary>
    private void ApplyEqualizer(IntPtr buffer, int frames, EqDesign? target, bool suspended)
    {
        if (_eqResetNext)
        {
            // The device was flushed: what comes next doesn't follow on from what
            // the filters last heard.
            _eqResetNext = false;
            _eqActive.Reset();
            _eqSpare.Reset();
        }

        if (suspended)
        {
            if (_eqApplied is not null)
            {
                _eqActive.Reset();
                _eqApplied = null;
            }
            return;
        }

        if (target is null && _eqApplied is null)
            return;

        var started = Stopwatch.GetTimestamp();
        int clipped;
        if (ReferenceEquals(target, _eqApplied))
        {
            clipped = Run(_eqActive, target!, buffer, 0, frames);
        }
        else
        {
            clipped = Crossfade(buffer, frames, _eqApplied, target);
            (_eqActive, _eqSpare) = (_eqSpare, _eqActive);
            _eqApplied = target;
        }

        PlaybackDiagnostics.EqualizerBlock(clipped, Stopwatch.GetTimestamp() - started);
    }

    /// <summary>
    /// The first <see cref="_eqFadeFrames"/> of the block from <paramref name="from"/>'s
    /// output to <paramref name="to"/>'s (null being the samples as they are), then
    /// the rest through <paramref name="to"/> alone, on the spare processor.
    /// </summary>
    private int Crossfade(IntPtr buffer, int frames, EqDesign? from, EqDesign? to)
    {
        var fade = Math.Min(_eqFadeFrames, frames);
        var samples = fade * _mixerChannels;
        Marshal.Copy(buffer, _eqOld, 0, samples);
        Array.Copy(_eqOld, _eqNew, samples);

        _eqSpare.Reset();
        if (from is not null)
            _eqActive.Process(from, _eqOld.AsSpan(0, samples));
        var clipped = to is not null ? _eqSpare.Process(to, _eqNew.AsSpan(0, samples)) : 0;

        for (var f = 0; f < fade; f++)
        {
            var t = (f + 1.0) / fade;
            var g = 0.5 - 0.5 * Math.Cos(Math.PI * t);
            for (var c = 0; c < _mixerChannels; c++)
            {
                var i = f * _mixerChannels + c;
                _eqNew[i] = (float)(_eqOld[i] * (1 - g) + _eqNew[i] * g);
            }
        }
        Marshal.Copy(_eqNew, 0, buffer, samples);

        if (to is not null && frames > fade)
            clipped += Run(_eqSpare, to, buffer, fade, frames - fade);
        return clipped;
    }

    /// <summary>Runs <paramref name="design"/> over <paramref name="count"/> frames from <paramref name="firstFrame"/>, a chunk at a time.</summary>
    private int Run(EqProcessor processor, EqDesign design, IntPtr buffer, int firstFrame, int count)
    {
        var clipped = 0;
        var chunkFrames = _eqOld.Length / _mixerChannels;
        for (var done = 0; done < count; done += chunkFrames)
        {
            var n = Math.Min(chunkFrames, count - done);
            var at = buffer + (firstFrame + done) * _mixerChannels * sizeof(float);
            var samples = n * _mixerChannels;
            Marshal.Copy(at, _eqOld, 0, samples);
            clipped += processor.Process(design, _eqOld.AsSpan(0, samples));
            Marshal.Copy(_eqOld, 0, at, samples);
        }
        return clipped;
    }

    /// <summary>
    /// The equalizer to run from the next block on, worked out for
    /// <see cref="MixerRate"/>; null (or a design that changes nothing) for none.
    /// </summary>
    public void SetEqualizer(EqDesign? design)
    {
        if (design is not null && design.SampleRate != MixerRate)
            throw new ArgumentException($"An equalizer for {design.SampleRate} Hz on a {MixerRate} Hz chain.", nameof(design));
        Volatile.Write(ref _eqDesign, design is { IsIdentity: false } ? design : null);
    }

    /// <summary>The equalizer asked for, or null.</summary>
    public EqDesign? Equalizer => Volatile.Read(ref _eqDesign);

    /// <summary>
    /// Set while the playing stream is DoP: the equalizer leaves the blocks that
    /// start on it alone, whatever is asked for.
    /// </summary>
    public bool EqualizerSuspended
    {
        get => _eqSuspended;
        set => _eqSuspended = value;
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

        // Exclusive mode is topped up on a timer, as shared mode is, rather than
        // event-driven (session 49). Event-driven pulled a whole 200 ms block each
        // time the device asked, and on the user's DAC a pull 33-39 ms late was an
        // audible gap (twice, both logged); shared mode rode out 60 ms ones unheard.
        // Same samples either way: only the timing of the deliveries changes. The
        // buffer must be at least 4x the period in exclusive mode (BASSWASAPI's docs).
        // A device that won't open that way gets event-driven, as before.
        var style = "timer";
        bool opened;
        if (mode == OutputMode.Exclusive)
        {
            opened = BassWasapi.Init(-1, sampleRate, channels, WasapiInitFlags.Exclusive | WasapiInitFlags.Buffer,
                BufferSeconds, ExclusivePeriodSeconds, chain._wasapiProc);
            if (!opened)
            {
                style = "event-driven";
                opened = BassWasapi.Init(-1, sampleRate, channels, WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven | WasapiInitFlags.Buffer,
                    BufferSeconds, 0f, chain._wasapiProc);
            }
        }
        else
        {
            opened = BassWasapi.Init(-1, sampleRate, channels, WasapiInitFlags.Shared | WasapiInitFlags.Buffer,
                BufferSeconds, 0f, chain._wasapiProc);
        }

        if (!opened)
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
        PlaybackDiagnostics.DeviceOpened(mode, chain.SampleRate, depth, BufferSeconds, style);
        return chain;
    }

    public bool Start()
    {
        PlaybackDiagnostics.DeviceStarting();
        return BassWasapi.Start();
    }

    /// <summary>Stops the device without discarding what's buffered.</summary>
    public void Pause() => BassWasapi.Stop(false);

    public void Resume()
    {
        PlaybackDiagnostics.DeviceStarting();
        BassWasapi.Start();
    }

    /// <summary>Stops the device and discards what's buffered.</summary>
    public void Stop()
    {
        BassWasapi.Stop(true);
        _eqResetNext = true;
    }

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

        PlaybackDiagnostics.DeviceClosed();
        BassWasapi.Stop(true);
        BassWasapi.Free();

        if (_mixer != 0)
        {
            Bass.StreamFree(_mixer);
            _mixer = 0;
        }
    }
}
