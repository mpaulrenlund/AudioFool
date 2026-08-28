using System.Collections.Concurrent;
using ManagedBass;
using ManagedBass.Dsd;
using ManagedBass.Mix;
using AudioFool.Core.Library;
using AudioFool.Core.Models;

namespace AudioFool.Core.Playback;

/// <summary>
/// Gapless playback engine.
/// <para>
/// The trick to gapless is that the audio device is never stopped between
/// tracks. One long-lived BASSmix mixer stays open for the whole session, and
/// each track is a <em>decode</em> channel fed into it. When a track runs dry,
/// a <c>Mixtime</c> sync fires from inside BASS's mixing pass and the
/// already-decoded next channel is spliced in on the spot - so the transition
/// costs zero samples of silence.
/// </para>
/// <para>
/// The next track's stream is created ahead of time on a worker thread, because
/// opening a file inside the audio callback would risk an underrun.
/// </para>
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>
    /// NoRampin suppresses BASS's default fade-in on a newly added channel, which
    /// would otherwise put a tiny volume dip at the start of every track - audible
    /// precisely at the boundaries gapless playback exists to hide.
    /// </summary>
    private const BassFlags SourceFlags = BassFlags.MixerChanNoRampin;

    private readonly BassRuntime _runtime;
    private readonly SynchronizationContext? _events;
    private readonly SyncProcedure _endSyncProc;
    private readonly ConcurrentDictionary<int, bool> _isModuleHandle = new();
    private readonly object _gate = new();

    private readonly HashSet<int> _exclusiveRefusedAt = [];
    private readonly ConcurrentDictionary<int, bool> _isDopHandle = new();

    private List<Track> _queue = [];
    private int _index = -1;
    private OutputChain? _output;
    private OutputMode _requestedMode = OutputMode.Shared;
    private DsdMode _dsdMode = DsdMode.ConvertToPcm;
    private int _currentStream;
    private int _prefetchedStream;
    private int _prefetchedIndex = -1;
    private double _volume = 0.7;
    private PlaybackState _state = PlaybackState.Stopped;
    private bool _disposed;

    public AudioEngine(BassRuntime runtime)
    {
        _runtime = runtime;

        // Held in a field so the GC can't collect the delegate BASS is calling.
        _endSyncProc = OnCurrentStreamEnded;

        // Captured so consumers get events on the thread that built the engine
        // (the UI thread) instead of a BASS worker thread.
        _events = SynchronizationContext.Current;
    }

    public event EventHandler<Track>? TrackChanged;
    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler? PlaybackFinished;

    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public PlaybackState State
    {
        get { lock (_gate) return _state; }
    }

    public Track? CurrentTrack
    {
        get
        {
            lock (_gate)
                return _index >= 0 && _index < _queue.Count ? _queue[_index] : null;
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_gate)
                _output?.SetVolume(_volume);
        }
    }

    /// <summary>
    /// Requested output mode. Takes effect on the next track, or immediately if
    /// something is playing (which costs a short gap while the device reopens).
    /// </summary>
    public OutputMode OutputMode
    {
        get { lock (_gate) return _requestedMode; }
        set
        {
            lock (_gate)
            {
                if (_requestedMode == value)
                    return;

                _requestedMode = value;

                // A mode the user re-selects deserves a fresh attempt even if the
                // device refused it earlier.
                _exclusiveRefusedAt.Clear();
            }

            // Reopen the device under the new mode, keeping our place in the queue.
            var index = _index;
            if (index >= 0 && State != PlaybackState.Stopped)
                JumpTo(index);
        }
    }

    /// <summary>
    /// How DSD material is handled. Reopens the current track if one is playing,
    /// since the choice is baked into the decode stream.
    /// </summary>
    public DsdMode DsdMode
    {
        get { lock (_gate) return _dsdMode; }
        set
        {
            int index;
            lock (_gate)
            {
                if (_dsdMode == value)
                    return;

                _dsdMode = value;
                index = _index;
            }

            if (index >= 0 && State != PlaybackState.Stopped)
                JumpTo(index);
        }
    }

    /// <summary>What the output is actually doing right now, for the status bar.</summary>
    public string OutputDescription
    {
        get
        {
            lock (_gate)
            {
                if (_output is null)
                    return "not started";

                // DoP looks like ordinary high-rate PCM from the device's side, so
                // say which it is rather than leaving it ambiguous.
                return _isDopHandle.ContainsKey(_currentStream)
                    ? $"{_output.Describe()} · DSD over PCM"
                    : _output.Describe();
            }
        }
    }

    /// <summary>True when the current track is being passed through as DoP.</summary>
    public bool IsDopActive
    {
        get { lock (_gate) return _currentStream != 0 && _isDopHandle.ContainsKey(_currentStream); }
    }

    /// <summary>
    /// Whether a device connection has been opened yet. The output description is
    /// meaningless before the first play, so the UI hides it until this is true.
    /// </summary>
    public bool HasOutput
    {
        get { lock (_gate) return _output is not null; }
    }

    /// <summary>True when the output is genuinely passing samples through untouched.</summary>
    public bool IsBitPerfect
    {
        get { lock (_gate) return _output?.Mode == OutputMode.Exclusive; }
    }

    /// <summary>
    /// False in exclusive mode, where attenuating in software would defeat the
    /// purpose. The UI disables its volume slider on this.
    /// </summary>
    public bool SupportsVolume
    {
        get { lock (_gate) return _output?.SupportsVolume ?? true; }
    }

    /// <summary>Set when exclusive mode was asked for but the device refused.</summary>
    public string? OutputWarning { get; private set; }

    /// <summary>Where we are in the current track, as the speakers hear it.</summary>
    public TimeSpan Position
    {
        get
        {
            var stream = Volatile.Read(ref _currentStream);
            if (stream == 0)
                return TimeSpan.Zero;

            var bytes = BassMix.ChannelGetPosition(stream);
            if (bytes < 0)
                return TimeSpan.Zero;

            var seconds = Bass.ChannelBytes2Seconds(stream, bytes);
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            var stream = Volatile.Read(ref _currentStream);
            if (stream == 0)
                return CurrentTrack?.Duration ?? TimeSpan.Zero;

            var bytes = Bass.ChannelGetLength(stream);
            if (bytes < 0)
                return CurrentTrack?.Duration ?? TimeSpan.Zero;

            var seconds = Bass.ChannelBytes2Seconds(stream, bytes);
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }
    }

    /// <summary>Starts a queue at the given index. Replaces anything already playing.</summary>
    public bool Play(IReadOnlyList<Track> queue, int startIndex)
    {
        if (!_runtime.Initialise() || queue.Count == 0)
            return false;

        if (startIndex < 0 || startIndex >= queue.Count)
            startIndex = 0;

        // Build the stream before touching engine state, so a bad file leaves
        // whatever is currently playing undisturbed.
        var track = queue[startIndex];
        var stream = CreateDecodeStream(track.FilePath);
        if (stream == 0)
            return false;

        lock (_gate)
        {
            // Tear the old streams down first: reopening the device below frees the
            // mixer they're plugged into.
            TeardownStreamsLocked();

            // In exclusive mode the device follows the source, so the rate has to be
            // known before the output can be opened.
            if (!EnsureOutput(RateOf(stream)) || _output is null)
            {
                FreeStream(stream);
                return false;
            }

            _queue = [.. queue];
            _index = startIndex;
            _currentStream = stream;

            BassMix.MixerAddChannel(_output.Mixer, stream, SourceFlags);
            Bass.ChannelSetSync(stream, SyncFlags.End | SyncFlags.Mixtime, 0, _endSyncProc);
            _output.Start();
        }

        SetState(PlaybackState.Playing);
        Raise(TrackChanged, track);
        PrefetchAfter(startIndex);
        return true;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != PlaybackState.Playing || _output is null)
                return;
            _output.Pause();
        }

        SetState(PlaybackState.Paused);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != PlaybackState.Paused || _output is null)
                return;
            _output.Resume();
        }

        SetState(PlaybackState.Playing);
    }

    public void TogglePause()
    {
        if (State == PlaybackState.Playing)
            Pause();
        else if (State == PlaybackState.Paused)
            Resume();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _output?.Stop();
            TeardownStreamsLocked();
            _index = -1;
        }

        SetState(PlaybackState.Stopped);
    }

    /// <summary>User-initiated skip: immediate, not gapless (which is what a skip should feel like).</summary>
    public bool Next()
    {
        int target;
        lock (_gate)
        {
            if (_queue.Count == 0)
                return false;

            target = _index + 1;
            if (target >= _queue.Count)
            {
                if (Repeat != RepeatMode.All)
                    return false;
                target = 0;
            }
        }

        return JumpTo(target);
    }

    /// <summary>
    /// Restarts the current track if we're more than three seconds in, otherwise
    /// steps back a track - the behaviour every other player has trained us to expect.
    /// </summary>
    public bool Previous()
    {
        if (Position > TimeSpan.FromSeconds(3))
        {
            Seek(TimeSpan.Zero);
            return true;
        }

        int target;
        lock (_gate)
        {
            if (_queue.Count == 0)
                return false;

            target = _index - 1;
            if (target < 0)
            {
                if (Repeat != RepeatMode.All)
                {
                    Seek(TimeSpan.Zero);
                    return true;
                }
                target = _queue.Count - 1;
            }
        }

        return JumpTo(target);
    }

    public bool JumpTo(int index)
    {
        List<Track> queue;
        lock (_gate)
            queue = _queue;

        return index >= 0 && index < queue.Count && Play(queue, index);
    }

    public void Seek(TimeSpan position)
    {
        var stream = Volatile.Read(ref _currentStream);
        if (stream == 0)
            return;

        var bytes = Bass.ChannelSeconds2Bytes(stream, Math.Max(0, position.TotalSeconds));
        if (bytes >= 0)
            BassMix.ChannelSetPosition(stream, bytes);
    }

    /// <summary>
    /// Fires from inside BASS's mixing pass the instant the current track's data
    /// runs out. Splicing the next channel in here is what makes it gapless, so
    /// this must stay short - anything slow is handed to the thread pool.
    /// </summary>
    private void OnCurrentStreamEnded(int syncHandle, int channel, int data, IntPtr user)
    {
        int finished;
        int promoted = 0;
        var promotedIndex = -1;
        var reopenAtIndex = -1;

        lock (_gate)
        {
            // A sync left over from a stream we've already replaced.
            if (channel != _currentStream)
                return;

            if (Repeat == RepeatMode.One && _output is not null)
            {
                // Rewind and re-add the same channel; its END sync is still attached.
                Bass.ChannelSetPosition(_currentStream, 0);
                BassMix.MixerAddChannel(_output.Mixer, _currentStream, SourceFlags);
                return;
            }

            finished = _currentStream;

            // The next track can only be spliced in if the device is already
            // running at its rate. In shared mode that's always true. In exclusive
            // mode a 44.1 kHz track following a 96 kHz one means re-clocking the
            // DAC, which cannot be done without a gap - so hand it to a worker to
            // reopen the device rather than pretending otherwise.
            var canSplice = _prefetchedStream != 0
                            && _output is not null
                            && RateOf(_prefetchedStream) == _output.SampleRate;

            if (canSplice)
            {
                promoted = _prefetchedStream;
                promotedIndex = _prefetchedIndex;
                _prefetchedStream = 0;
                _prefetchedIndex = -1;

                _currentStream = promoted;
                _index = promotedIndex;

                BassMix.MixerAddChannel(_output!.Mixer, promoted, SourceFlags);
                Bass.ChannelSetSync(promoted, SyncFlags.End | SyncFlags.Mixtime, 0, _endSyncProc);
            }
            else
            {
                if (_prefetchedStream != 0)
                    reopenAtIndex = _prefetchedIndex;

                _currentStream = 0;
            }
        }

        // Off the audio thread from here on.
        Task.Run(() =>
        {
            // BASSmix does not unplug a source when it reaches its end - it just
            // stalls, contributing silence. Freeing it unplugs it for us.
            FreeStream(finished);

            if (promoted != 0)
            {
                var track = CurrentTrack;
                if (track is not null)
                    Raise(TrackChanged, track);

                PrefetchAfter(promotedIndex);
            }
            else if (reopenAtIndex >= 0)
            {
                // Sample rate changed: drop the prefetch (it's plugged into a mixer
                // that's about to be freed) and restart cleanly at the new rate.
                lock (_gate)
                {
                    FreeStream(_prefetchedStream);
                    _prefetchedStream = 0;
                    _prefetchedIndex = -1;
                }

                JumpTo(reopenAtIndex);
            }
            else
            {
                SetState(PlaybackState.Stopped);
                Raise(PlaybackFinished);
            }
        });
    }

    /// <summary>
    /// Opens the track after <paramref name="currentIndex"/> so the gapless
    /// handover has a decoded stream waiting for it.
    /// </summary>
    private void PrefetchAfter(int currentIndex)
    {
        Task.Run(() =>
        {
            int target;
            string path;

            lock (_gate)
            {
                // The user skipped while we were being scheduled; the successor
                // of this index is no longer the one we want.
                if (_index != currentIndex)
                    return;

                target = currentIndex + 1;
                if (target >= _queue.Count)
                {
                    if (Repeat != RepeatMode.All || _queue.Count == 0)
                        return;
                    target = 0;
                }

                path = _queue[target].FilePath;
            }

            // File I/O outside the lock.
            var stream = CreateDecodeStream(path);
            if (stream == 0)
                return;

            var discard = 0;
            lock (_gate)
            {
                if (_index != currentIndex)
                {
                    discard = stream;   // skipped mid-open after all
                }
                else
                {
                    if (_prefetchedStream != 0)
                        discard = _prefetchedStream;

                    _prefetchedStream = stream;
                    _prefetchedIndex = target;
                }
            }

            if (discard != 0)
                FreeStream(discard);
        });
    }

    /// <summary>
    /// Makes sure a device connection exists at the right rate, reopening it only
    /// when the rate actually has to change. Caller must hold <see cref="_gate"/>.
    /// </summary>
    private bool EnsureOutput(int sourceRate)
    {
        // Shared mode is pinned to the rate Windows mixes at, so the chain is built
        // once and every track is gapless. Exclusive mode follows the source.
        var wantExclusive = _requestedMode == OutputMode.Exclusive
                            && sourceRate > 0
                            && !_exclusiveRefusedAt.Contains(sourceRate);

        var desiredRate = wantExclusive ? sourceRate : _runtime.SharedMixRate;
        var desiredMode = wantExclusive ? OutputMode.Exclusive : OutputMode.Shared;

        if (_output is not null && _output.SampleRate == desiredRate && _output.Mode == desiredMode)
            return true;

        _output?.Dispose();
        _output = null;

        if (wantExclusive)
        {
            _output = OutputChain.TryCreate(OutputMode.Exclusive, desiredRate, 2, out var exclusiveError);

            if (_output is null)
            {
                // Remember the refusal so every subsequent track doesn't pay for
                // another failed init attempt at the same rate.
                _exclusiveRefusedAt.Add(sourceRate);
                OutputWarning = $"{exclusiveError} Falling back to shared output.";
            }
            else
            {
                OutputWarning = null;
            }
        }

        // Not evaluated at all when exclusive already succeeded, hence the
        // pre-initialised error variable.
        string? sharedError = null;
        _output ??= OutputChain.TryCreate(OutputMode.Shared, _runtime.SharedMixRate, 2, out sharedError);

        if (_output is null)
        {
            OutputWarning = sharedError;
            return false;
        }

        _output.SetVolume(_volume);
        return true;
    }

    /// <summary>Sample rate of a decode channel, or 0 if it can't be determined.</summary>
    private static int RateOf(int stream) =>
        stream != 0 && Bass.ChannelGetInfo(stream, out var info) ? info.Frequency : 0;

    private int CreateDecodeStream(string path)
    {
        if (!File.Exists(path))
            return 0;

        var flags = BassFlags.Decode | BassFlags.Float;

        if (AudioFormats.IsModule(path))
        {
            var music = Bass.MusicLoad(path, 0, 0, flags | BassFlags.MusicRamp, 0);
            if (music != 0)
                _isModuleHandle[music] = true;
            return music;
        }

        if (AudioFormats.IsDsd(path))
        {
            var dop = TryCreateDopStream(path, flags);
            if (dop != 0)
                return dop;

            // Fall through: BASSDSD converts to PCM at Configuration.DSDFrequency.
        }

        // Prescan: without it, seeking and length on a VBR MP3 are estimates.
        if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            flags |= BassFlags.Prescan;

        return Bass.CreateStream(path, 0, 0, flags);
    }

    /// <summary>
    /// Opens a DSD file as DSD-over-PCM, or returns 0 if that isn't possible here.
    /// <para>
    /// DoP only means anything if the bits survive the trip to the DAC, so it is
    /// refused unless the output is exclusive (no resampling, unity gain) and the
    /// device can actually clock the DoP rate. Anything else and the markers would
    /// be mangled into noise, so falling back to PCM conversion is the right answer.
    /// </para>
    /// </summary>
    private int TryCreateDopStream(string path, BassFlags baseFlags)
    {
        if (_dsdMode != DsdMode.DsdOverPcm || _requestedMode != OutputMode.Exclusive)
            return 0;

        // Float is mandatory for DoP: the payload is 24-bit, which BASS has no
        // native integer format for, and float32 holds 24-bit values exactly.
        var stream = BassDsd.CreateStream(path, 0, 0, baseFlags | BassFlags.DSDOverPCM);
        if (stream == 0)
            return 0;

        var dopRate = RateOf(stream);
        if (dopRate > 0 && _runtime.ExclusiveRates.Contains(dopRate))
        {
            _isDopHandle[stream] = true;
            return stream;
        }

        // The DAC can't take DoP at this DSD rate.
        Bass.StreamFree(stream);
        return 0;
    }

    private void FreeStream(int handle)
    {
        if (handle == 0)
            return;

        _isDopHandle.TryRemove(handle, out _);

        if (_isModuleHandle.TryRemove(handle, out _))
            Bass.MusicFree(handle);
        else
            Bass.StreamFree(handle);
    }

    /// <summary>Detaches and frees both streams. Caller must hold <see cref="_gate"/>.</summary>
    private void TeardownStreamsLocked()
    {
        if (_currentStream != 0)
        {
            BassMix.MixerRemoveChannel(_currentStream);
            FreeStream(_currentStream);
            _currentStream = 0;
        }

        if (_prefetchedStream != 0)
        {
            FreeStream(_prefetchedStream);
            _prefetchedStream = 0;
            _prefetchedIndex = -1;
        }
    }

    private void SetState(PlaybackState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;
            _state = state;
        }

        Raise(StateChanged, state);
    }

    private void Raise<T>(EventHandler<T>? handler, T arg)
    {
        if (handler is null)
            return;

        if (_events is null)
            handler(this, arg);
        else
            _events.Post(_ => handler(this, arg), null);
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null)
            return;

        if (_events is null)
            handler(this, EventArgs.Empty);
        else
            _events.Post(_ => handler(this, EventArgs.Empty), null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_gate)
        {
            _output?.Stop();
            TeardownStreamsLocked();

            // Disposing the chain frees the mixer, so the streams must go first.
            _output?.Dispose();
            _output = null;
        }
    }
}
