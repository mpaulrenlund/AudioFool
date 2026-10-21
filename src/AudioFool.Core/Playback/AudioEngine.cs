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
public sealed class AudioEngine : IFileHolder, IDisposable
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

    private readonly PlayOrder _order = new();

    private List<Track> _queue = [];
    private int _index = -1;
    private bool _shuffle;
    private RepeatMode _repeat = RepeatMode.Off;
    private OutputChain? _output;
    private OutputMode _requestedMode = OutputMode.Shared;
    private int _currentStream;
    private int _prefetchedStream;
    private int _prefetchedIndex = -1;
    private double _volume = 0.7;
    private PlaybackState _state = PlaybackState.Stopped;
    private bool _disposed;

    // While a save has the playing track's file: where it was, in ticks, or -1.
    private long _releasedAtTicks = -1;

    // While a save has the next track's file: its index, or -1. If the playing
    // track ends meanwhile, the handover waits for the save (_advanceAfterRelease).
    private int _releasedNextIndex = -1;
    private bool _advanceAfterRelease;

    private readonly object _releaseGate = new();

    // The playing stream and its position just before the device's current pull.
    // Written and read on the device's thread only (BeforePull, then the END sync
    // inside that pull), so no lock. See SpliceDelay.
    private readonly Action _notePullStart;
    private int _pullStream;
    private long _pullStartBytes = -1;

    // What the device has been given, so what is heard can be told from what is
    // decoded (see Heard). The fields below it are the device's thread's own,
    // like _pullStream: the track and spot the current pull started at, and
    // where in it the END sync switched track.
    private readonly HearingTimeline _heard = new();
    private readonly SampleTap _tap = new();
    private readonly Action<IntPtr, int, bool> _notePullEnd;
    private Track? _pullTrack;
    private double _pullStartSeconds;
    private long _joinFrames = -1;
    private Track? _joinTrack;

    // The current stream and its track as one, written with _currentStream so the
    // device's thread reads a matching pair without the lock.
    private sealed record PlayingStream(int Stream, Track Track);
    private PlayingStream? _playing;

    // Counts the plays PlayCore starts, so an announcement waiting for the
    // speakers can tell that one has superseded it.
    private int _plays;

    // What was heard when the device paused: it holds still until Resume.
    private sealed record HeardAtPause(HeardPoint Point);
    private HeardAtPause? _heardAtPause;

    /// <summary>
    /// How long a track change waits for the speakers to reach it, at most: well
    /// past the device's 200 ms, in case the device can't say what it holds.
    /// </summary>
    private static readonly TimeSpan MaxHeardWait = TimeSpan.FromSeconds(2);

    // Files a save is writing, and files a stream is being opened on (a file can
    // be opened twice at once: the playing track and, on repeat, the next).
    // Both are guarded by _gate, which is also what waits on them.
    private readonly HashSet<string> _writing = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _opening = [];

    public AudioEngine(BassRuntime runtime)
    {
        _runtime = runtime;

        // Held in a field so the GC can't collect the delegate BASS is calling.
        _endSyncProc = OnCurrentStreamEnded;
        _notePullStart = NotePullStart;
        _notePullEnd = NotePullEnd;

        // Captured so consumers get events on the thread that built the engine
        // (the UI thread) instead of a BASS worker thread.
        _events = SynchronizationContext.Current;

        TrackChanged += (_, track) => PlaybackDiagnostics.TrackStarted(track.FilePath);
    }

    public event EventHandler<Track>? TrackChanged;
    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler? PlaybackFinished;

    /// <summary>
    /// What happens at the end of the queue. Changing this changes which track
    /// comes next, so the stream opened ahead of time has to be re-opened.
    /// </summary>
    public RepeatMode Repeat
    {
        get { lock (_gate) return _repeat; }
        set
        {
            lock (_gate)
            {
                if (_repeat == value)
                    return;

                _repeat = value;
            }

            RefreshPrefetch();
        }
    }

    /// <summary>
    /// Whether the queue plays in a shuffled order. Toggling this never changes
    /// what is playing right now - the current track is pinned to the front of
    /// the new order and the rest falls in behind it.
    /// </summary>
    public bool Shuffle
    {
        get { lock (_gate) return _shuffle; }
        set
        {
            lock (_gate)
            {
                if (_shuffle == value)
                    return;

                _shuffle = value;
                _order.Reset(_queue.Count, _index, _shuffle);
            }

            RefreshPrefetch();
        }
    }

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

    /// <summary>
    /// Whether a stream is decoding <paramref name="path"/> right now: the current
    /// track (paused included) or the next one, opened ahead for the gapless
    /// handover. A tag save that resizes such a file moves the audio under the
    /// stream, so <see cref="Library.TagWriter"/> asks first.
    /// </summary>
    public bool HoldsFile(string path)
    {
        lock (_gate)
        {
            bool Is(int index) => index >= 0 && index < _queue.Count
                && string.Equals(_queue[index].FilePath, path, StringComparison.OrdinalIgnoreCase);

            return (_currentStream != 0 && Is(_index)) || (_prefetchedStream != 0 && Is(_prefetchedIndex));
        }
    }

    /// <summary>
    /// Makes a tag save to <paramref name="path"/>. No stream opens the file
    /// until the save is done: one that would (Next onto the track, the next
    /// track opened ahead) waits for it, so it never reads a half-written file.
    /// A save waits in turn for an open already under way, so that
    /// <see cref="HoldsFile"/> is the truth when it is asked.
    /// <para>
    /// If a stream holds the file and <paramref name="needsRelease"/> says the
    /// save would move the audio under it, the streams are closed for the save
    /// and reopened on the rewritten file. The playing track stops (paused
    /// stays paused), and comes back at the position it was at, faded in so it
    /// doesn't click. Nothing is announced: to the rest of the app it is the
    /// same play of the same track, so the scrobbler and the waveform carry on.
    /// The next track's stream is only ahead of time, so dropping and reopening
    /// it is silent; if the playing track ends before the save does, the
    /// handover waits for it. If anything else starts playing meanwhile, that
    /// wins and nothing is reopened.
    /// </para>
    /// </summary>
    public T Saving<T>(string path, Func<bool> needsRelease, Func<T> save)
    {
        lock (_releaseGate)
        {
            bool held;
            lock (_gate)
            {
                while (_opening.Contains(path, StringComparer.OrdinalIgnoreCase))
                    Monitor.Wait(_gate);
                _writing.Add(path);
                held = HoldsFile(path);
            }

            try
            {
                if (!held || !needsRelease())
                    return save();

                var released = Release(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase), closeDevice: false);
                try
                {
                    return save();
                }
                finally
                {
                    // Before reopening: Reacquire opens this very file.
                    EndWriting(path);
                    Reacquire(released);
                }
            }
            finally
            {
                EndWriting(path);
            }
        }
    }

    private void EndWriting(string path)
    {
        lock (_gate)
        {
            if (_writing.Remove(path))
                Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// <see cref="CreateDecodeStream"/>, held off while a save is writing the
    /// file, and marked as opening so a save waits for it in turn.
    /// </summary>
    private int OpenStream(string path)
    {
        lock (_gate)
        {
            while (_writing.Contains(path))
                Monitor.Wait(_gate);
            _opening.Add(path);
        }

        try
        {
            return CreateDecodeStream(path);
        }
        finally
        {
            lock (_gate)
            {
                _opening.Remove(path);
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>
    /// Moves output to the Windows default device, which has just changed, in
    /// <paramref name="mode"/>: the caller has worked out whether the new device
    /// can do exclusive mode. The device connection is closed and opened again,
    /// and the playing track carries on from where it was (paused stays paused),
    /// the same way <see cref="Saving{T}"/> brings it back. Its stream is
    /// opened again too, since the device decides what DSD becomes.
    /// </summary>
    public void SwitchDevice(OutputMode mode)
    {
        lock (_releaseGate)
        {
            lock (_gate)
            {
                _requestedMode = mode;
                _exclusiveRefusedAt.Clear();
                OutputWarning = null;
            }

            Reacquire(Release(_ => true, closeDevice: true));
        }
    }

    private sealed record Released(List<Track> Queue, int Index, TimeSpan At, bool Paused);

    /// <summary>
    /// Frees the streams reading a file <paramref name="matches"/> accepts, and with
    /// <paramref name="closeDevice"/> the device connection too. Returns the playing
    /// track's place if it was one of them.
    /// </summary>
    private Released? Release(Func<string, bool> matches, bool closeDevice)
    {
        Released? current = null;
        var discard = 0;

        lock (_gate)
        {
            bool Is(int index) => index >= 0 && index < _queue.Count && matches(_queue[index].FilePath);

            if (_prefetchedStream != 0 && Is(_prefetchedIndex))
            {
                discard = _prefetchedStream;
                _releasedNextIndex = _prefetchedIndex;
                _prefetchedStream = 0;
                _prefetchedIndex = -1;
            }

            if (_currentStream != 0 && Is(_index))
            {
                var paused = _state == PlaybackState.Paused;
                current = new Released(_queue, _index, Position, paused);
                Interlocked.Exchange(ref _releasedAtTicks, current.At.Ticks);

                // As PlayCore does: a playing device is flushed, so the old stream's
                // buffered tail doesn't play on; a paused one can't be flushed and
                // is closed, and reopened when the track comes back.
                if (!paused)
                    _output?.Stop();

                BassMix.MixerRemoveChannel(_currentStream);
                FreeStream(_currentStream);
                SetCurrentStream(0);

                if (paused)
                {
                    _output?.Dispose();
                    _output = null;
                }
            }

            // Nothing loaded (stopped): the next play opens the new device.
            if (closeDevice && _output is not null)
            {
                if (current is null)
                    _output.Stop();
                _output.Dispose();
                _output = null;
            }
        }

        FreeStream(discard);
        return current;
    }

    /// <summary>Reopens what <see cref="Release"/> let go of, unless playback has moved on.</summary>
    private void Reacquire(Released? current)
    {
        var stopped = false;

        if (current is not null)
        {
            var track = current.Queue[current.Index];
            var stream = OpenStream(track.FilePath);

            lock (_gate)
            {
                Interlocked.Exchange(ref _releasedAtTicks, -1);

                var unchanged = ReferenceEquals(_queue, current.Queue) && _index == current.Index && _currentStream == 0;
                if (!unchanged)
                {
                    FreeStream(stream);
                }
                else if (stream == 0 || !EnsureOutput(RateOf(stream)) || _output is null)
                {
                    // The rewritten file won't open: stop rather than sit silent.
                    FreeStream(stream);
                    stopped = true;
                }
                else
                {
                    var bytes = Bass.ChannelSeconds2Bytes(stream, current.At.TotalSeconds);
                    if (bytes > 0)
                        Bass.ChannelSetPosition(stream, bytes);

                    SetCurrentStream(stream);
                    BassMix.MixerAddChannel(_output.Mixer, stream, SourceFlags);
                    Bass.ChannelSetSync(stream, SyncFlags.End | SyncFlags.Mixtime, 0, _endSyncProc);

                    if (!current.Paused)
                    {
                        // DoP is never faded; see Seek.
                        if (!_isDopHandle.ContainsKey(stream))
                            _output.FadeInNextBlock();
                        _output.Start();
                    }
                }
            }
        }

        int advanceTo, refetchAfter = -1;
        lock (_gate)
        {
            advanceTo = _advanceAfterRelease ? _releasedNextIndex : -1;
            if (!_advanceAfterRelease && _releasedNextIndex >= 0 && _prefetchedStream == 0 && _currentStream != 0)
                refetchAfter = _index;

            _advanceAfterRelease = false;
            _releasedNextIndex = -1;
        }

        if (stopped)
            Stop();
        else if (advanceTo >= 0)
            JumpTo(advanceTo);
        else if (refetchAfter >= 0)
            PrefetchAfter(refetchAfter);
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
    /// Requested output mode. The device reopens under it straight away, the way
    /// <see cref="SwitchDevice"/> does: a playing track carries on from where it
    /// was after a short gap, a paused one stays paused, and stopped stays stopped.
    /// Changing the mode never starts playback.
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
            }

            // Also clears an earlier refusal: a mode the user re-selects deserves
            // a fresh attempt.
            SwitchDevice(value);
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

                return OutputReadout.Describe(
                    _output.Mode, _output.SampleRate, _output.DeviceBitDepth,
                    RateOf(_currentStream), _isDopHandle.ContainsKey(_currentStream));
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

    /// <summary>
    /// Where the decoder is in the current track: about 200 ms ahead of what the
    /// speakers play, the audio the device holds. What the engine itself works
    /// from (Previous's three seconds, a save's reopen); for showing, see
    /// <see cref="Heard"/>.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            // The playing track's file is out for a save: hold the bar still.
            var released = Interlocked.Read(ref _releasedAtTicks);
            if (released >= 0)
                return TimeSpan.FromTicks(released);

            var stream = Volatile.Read(ref _currentStream);
            if (stream == 0)
                return TimeSpan.Zero;

            // A seek waiting for the device's next pull already counts: reading
            // the old spot here would put it back on the seek bar for a moment.
            var bytes = Volatile.Read(ref _output)?.PendingSeekBytes(stream) ?? BassMix.ChannelGetPosition(stream);
            if (bytes < 0)
                return TimeSpan.Zero;

            var seconds = Bass.ChannelBytes2Seconds(stream, bytes);
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// What the speakers are playing now: the decode position less the audio the
    /// device still holds (session 45's lead, ~200 ms), read through
    /// <see cref="HearingTimeline"/>. Across a gapless join it is the old track
    /// until its last frame is heard. A seek shows its target straight away, and a
    /// pause or a save holds it still, as <see cref="Position"/> does. Falls back
    /// to the decode position when the device can't say what it holds. Any thread.
    /// </summary>
    public HeardPoint Heard
    {
        get
        {
            var released = Interlocked.Read(ref _releasedAtTicks);
            if (released >= 0)
                return new HeardPoint(CurrentTrack, TimeSpan.FromTicks(released));

            var stream = Volatile.Read(ref _currentStream);
            if (stream != 0 && Volatile.Read(ref _output)?.PendingSeekBytes(stream) is not null)
                return new HeardPoint(CurrentTrack, Position);

            if (Volatile.Read(ref _heardAtPause) is { } paused)
                return paused.Point;

            var generation = _heard.Generation;
            var frame = HeardFrame();
            if (frame >= 0 && _heard.TryLookup(frame, out var track, out var seconds) && _heard.Generation == generation)
                return new HeardPoint(track, TimeSpan.FromSeconds(Math.Max(0, seconds)));

            return new HeardPoint(CurrentTrack, Position);
        }
    }

    /// <summary>
    /// The frame the speakers are at, counted as <see cref="HearingTimeline.TotalFrames"/>
    /// is, or -1 when the device can't say. Read twice around the device's figure
    /// so a pull landing in between doesn't skew it by a block.
    /// </summary>
    private long HeardFrame()
    {
        var output = Volatile.Read(ref _output);
        if (output is null)
            return -1;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var total = _heard.TotalFrames;
            var queued = output.QueuedFrames;
            if (queued < 0)
                return -1;
            if (_heard.TotalFrames == total)
                return Math.Max(0, total - queued);
        }

        return -1;
    }

    /// <summary>
    /// Whether what the device is given is copied for the visualizer
    /// (<see cref="SampleTap"/>). Off by default; off, the device's thread does
    /// nothing extra.
    /// </summary>
    public bool TapEnabled
    {
        get => _tap.IsEnabled;
        set => _tap.Enable(value);
    }

    /// <summary>The layout of the copy (the device's rate and channels), or null while off.</summary>
    public TapFormat? TapFormat => _tap.Format;

    /// <summary>
    /// Copies the frames the speakers have just played into
    /// <paramref name="destination"/>, the newest last, as the device was given
    /// them before the volume (DoP stays DoP). <paramref name="framesAhead"/> moves
    /// the window that far past what is heard, into what the device holds but
    /// hasn't played (up to its ~200 ms), so a window can be centred on now. False
    /// when they can't be had: the tap off or just turned on, the device reopened,
    /// or the device unable to say what it holds. Any thread.
    /// </summary>
    public bool TryReadHeard(float[] destination, TapFormat format, int framesAhead = 0)
    {
        var generation = _heard.Generation;
        var frame = HeardFrame();
        return frame >= 0 && _tap.TryRead(frame + Math.Max(0, framesAhead), destination, format) && _heard.Generation == generation;
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
    public bool Play(IReadOnlyList<Track> queue, int startIndex) =>
        PlayCore(queue, startIndex, resetOrder: true);

    /// <summary>
    /// <paramref name="resetOrder"/> separates a genuinely new queue from a move
    /// within the one already loaded. Only a new queue may rebuild the play order:
    /// re-shuffling on every track change would make Next unpredictable and
    /// Previous unable to retrace its steps.
    /// </summary>
    private bool PlayCore(IReadOnlyList<Track> queue, int startIndex, bool resetOrder)
    {
        if (!_runtime.Initialise() || queue.Count == 0)
            return false;

        if (startIndex < 0 || startIndex >= queue.Count)
            startIndex = 0;

        // Build the stream before touching engine state, so a bad file leaves
        // whatever is currently playing undisturbed.
        var track = queue[startIndex];
        var stream = OpenStream(track.FilePath);
        if (stream == 0)
            return false;

        lock (_gate)
        {
            // Flush the device first. Its buffer still holds up to BufferSeconds of
            // the old track, already pulled from the mixer, and unplugging the old
            // stream does not take that back - so without this the old track played
            // on for about 200 ms under the new one. Gapless handovers never come
            // through here, so they keep their continuity.
            //
            // A paused device keeps its buffer for Resume, and BASSWASAPI cannot
            // flush a device that is not running (Stop(true) fails), so a paused
            // chain is closed below and reopened by EnsureOutput instead.
            var reopen = _state == PlaybackState.Paused;
            if (!reopen)
                _output?.Stop();

            // Tear the old streams down first: reopening the device below frees the
            // mixer they're plugged into.
            TeardownStreamsLocked();

            if (reopen)
            {
                _output?.Dispose();
                _output = null;
            }

            // In exclusive mode the device follows the source, so the rate has to be
            // known before the output can be opened.
            if (!EnsureOutput(RateOf(stream)) || _output is null)
            {
                FreeStream(stream);
                return false;
            }

            // Anything a save was holding back is superseded by this play.
            _releasedNextIndex = -1;
            _advanceAfterRelease = false;

            _queue = [.. queue];
            _index = startIndex;
            SetCurrentStream(stream);
            Volatile.Write(ref _heardAtPause, null);
            Interlocked.Increment(ref _plays);

            if (resetOrder || _order.Count != _queue.Count)
                _order.Reset(_queue.Count, startIndex, _shuffle);

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

            // Read before the device stops: what it holds stays put until Resume.
            Volatile.Write(ref _heardAtPause, new HeardAtPause(Heard));
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
            Volatile.Write(ref _heardAtPause, null);
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
            Volatile.Write(ref _heardAtPause, null);
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

            target = _order.Next(_index, wrap: _repeat == RepeatMode.All);
            if (target < 0)
                return false;
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

            target = _order.Previous(_index, wrap: _repeat == RepeatMode.All);
            if (target < 0)
            {
                // Start of the order with no wrap: restart rather than stop, which
                // is what a second press of Previous should feel like.
                Seek(TimeSpan.Zero);
                return true;
            }
        }

        return JumpTo(target);
    }

    public bool JumpTo(int index)
    {
        List<Track> queue;
        lock (_gate)
            queue = _queue;

        return index >= 0 && index < queue.Count && PlayCore(queue, index, resetOrder: false);
    }

    /// <summary>
    /// Throws away the stream opened ahead of time and opens the right one instead.
    /// Called when something changes which track comes next - shuffle or repeat -
    /// while a track is already playing.
    /// </summary>
    private void RefreshPrefetch()
    {
        int discard;
        int index;

        lock (_gate)
        {
            discard = _prefetchedStream;
            _prefetchedStream = 0;
            _prefetchedIndex = -1;
            index = _index;
        }

        if (discard != 0)
            FreeStream(discard);

        if (index >= 0 && State != PlaybackState.Stopped)
            PrefetchAfter(index);
    }

    /// <summary>
    /// The jump is made by the output between two device pulls, faded out and back
    /// in, so it doesn't click. DoP is never faded: scaling its samples would
    /// break the markers the DAC reads them by.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            var stream = _currentStream;
            if (stream == 0)
                return;

            var bytes = Bass.ChannelSeconds2Bytes(stream, Math.Max(0, position.TotalSeconds));
            if (bytes < 0)
                return;

            if (_output is not null)
                _output.SeekBetweenBlocks(stream, bytes, fade: !_isDopHandle.ContainsKey(stream));
            else
                BassMix.ChannelSetPosition(stream, bytes);
        }
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
        var waitForSave = false;
        long spliceFrames = 0;
        var resampledJoin = false;
        long joinFrame = 0;
        var generation = _heard.Generation;
        var plays = Volatile.Read(ref _plays);

        lock (_gate)
        {
            // A sync left over from a stream we've already replaced.
            if (channel != _currentStream)
                return;

            if (_repeat == RepeatMode.One && _output is not null)
            {
                // The rest of this pull is the same track from its start.
                _joinFrames = FramesGivenThisPull(_currentStream);
                _joinTrack = _pullTrack;

                // Rewind and re-add the same channel; its END sync is still attached.
                Bass.ChannelSetPosition(_currentStream, 0);
                BassMix.MixerAddChannel(_output.Mixer, _currentStream, SourceFlags);
                return;
            }

            finished = _currentStream;

            // In shared mode the mixer runs at Windows' rate and resamples every
            // track to it, so any track can be spliced in. In exclusive mode the
            // device runs at the track's own rate: a 44.1 kHz track following a
            // 96 kHz one means re-clocking the DAC, which cannot be done without a
            // gap - so hand it to a worker to reopen the device rather than
            // pretending otherwise. (Until session 60 shared mode was held to the
            // same rule, and a track at another rate than Windows' lost the last
            // 0.2 s of the one before it to a reopen.)
            var canSplice = _prefetchedStream != 0
                            && _output is not null
                            && (_output.Mode == OutputMode.Shared || RateOf(_prefetchedStream) == _output.MixerRate);

            if (canSplice)
            {
                promoted = _prefetchedStream;
                promotedIndex = _prefetchedIndex;
                _prefetchedStream = 0;
                _prefetchedIndex = -1;

                _index = promotedIndex;
                SetCurrentStream(promoted);

                var delay = SpliceDelay(finished);
                spliceFrames = delay / (sizeof(float) * _output!.MixerChannels);
                resampledJoin = RateOf(promoted) != _output.MixerRate || RateOf(finished) != _output.MixerRate;

                // Where the new track starts in what the device is given: the
                // speakers reach it this far into this pull.
                _joinFrames = spliceFrames;
                _joinTrack = _playing?.Track;
                joinFrame = _heard.TotalFrames + spliceFrames;
                if (delay > 0)
                    BassMix.MixerAddChannel(_output!.Mixer, promoted, SourceFlags, delay, 0);
                else
                    BassMix.MixerAddChannel(_output!.Mixer, promoted, SourceFlags);
                Bass.ChannelSetSync(promoted, SyncFlags.End | SyncFlags.Mixtime, 0, _endSyncProc);
            }
            else
            {
                if (_prefetchedStream != 0)
                    reopenAtIndex = _prefetchedIndex;
                else if (_releasedNextIndex >= 0)
                    waitForSave = _advanceAfterRelease = true;   // Saving plays it

                // Silence from here on in this pull.
                _joinFrames = FramesGivenThisPull(finished);
                _joinTrack = null;

                SetCurrentStream(0);
            }
        }

        // Off the audio thread from here on.
        Task.Run(async () =>
        {
            // BASSmix does not unplug a source when it reaches its end - it just
            // stalls, contributing silence. Freeing it unplugs it for us.
            FreeStream(finished);

            if (promoted != 0)
            {
                var track = CurrentTrack;
                var resampled = resampledJoin ? " (resampled)" : "";
                PlaybackDiagnostics.Note(spliceFrames > 0
                    ? $"gapless: next track joined {spliceFrames} frames into the block{resampled}"
                    : $"gapless: next track joined at the block start (ended on a block edge){resampled}");

                PrefetchAfter(promotedIndex);

                // The old track is still playing from the device's buffer: announce
                // the new one when it is heard, unless something else has started
                // meanwhile (a save reopening this track doesn't count).
                bool StillCurrent() => Volatile.Read(ref _plays) == plays && ReferenceEquals(CurrentTrack, track);

                await WaitUntilHeardAsync(joinFrame, generation, StillCurrent);
                if (track is not null && StillCurrent())
                    Raise(TrackChanged, track);
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
            else if (waitForSave)
            {
                // The next track's file is mid-save; Reacquire moves on to it.
            }
            else
            {
                SetState(PlaybackState.Stopped);
                Raise(PlaybackFinished);
            }
        });
    }

    private void NotePullStart()
    {
        var playing = Volatile.Read(ref _playing);
        var stream = playing?.Stream ?? 0;
        _pullStream = stream;
        _pullStartBytes = stream != 0 ? Bass.ChannelGetPosition(stream) : -1;

        _pullTrack = _pullStartBytes >= 0 ? playing!.Track : null;
        _pullStartSeconds = _pullStartBytes >= 0 ? Math.Max(0, Bass.ChannelBytes2Seconds(stream, _pullStartBytes)) : 0;
        _joinFrames = -1;
        _joinTrack = null;
    }

    /// <summary>
    /// Notes what the device was just given (<see cref="HearingTimeline"/>): the
    /// track it came from and where in it, split where the END sync switched to
    /// the next track or to silence during the pull. A seek made at the end of the
    /// pull is noted with its target, read back from the stream it moved.
    /// </summary>
    private void NotePullEnd(IntPtr buffer, int frames, bool sought)
    {
        // Numbered as the timeline numbers them: this block starts at its total.
        _tap.Write(_heard.TotalFrames, buffer, frames);

        var join = _joinFrames;
        if (join >= 0 && join < frames)
        {
            _heard.Append(_pullTrack, _pullStartSeconds, (int)join);
            _heard.Append(_joinTrack, 0, frames - (int)join);
        }
        else
        {
            _heard.Append(_pullTrack, _pullStartSeconds, frames);
        }

        if (sought && Volatile.Read(ref _playing) is { } playing)
        {
            var bytes = Bass.ChannelGetPosition(playing.Stream);
            if (bytes >= 0)
                _heard.MarkSeek(playing.Track, Math.Max(0, Bass.ChannelBytes2Seconds(playing.Stream, bytes)));
        }
    }

    /// <summary>
    /// The mixer frames <paramref name="finished"/> gave in the pull under way
    /// before it ran out: the same reading as <see cref="SpliceDelay"/>, but
    /// scaled to the mixer's rate when resampled, since it only places the end
    /// for <see cref="Heard"/>. 0 when it can't be worked out.
    /// Device's thread, inside the END sync.
    /// </summary>
    private long FramesGivenThisPull(int finished)
    {
        if (_output is null || _pullStream != finished || _pullStartBytes < 0 || !Bass.ChannelGetInfo(finished, out var info)
            || info.Frequency <= 0)
            return 0;

        var sourceFrames = (Bass.ChannelGetPosition(finished) - _pullStartBytes) / (sizeof(float) * info.Channels);
        var frames = sourceFrames * _output.SampleRate / info.Frequency;
        return frames > 0 && frames <= _output.SampleRate ? frames : 0;
    }

    /// <summary>
    /// Returns once the speakers have reached <paramref name="frame"/>, the device
    /// connection has changed, <paramref name="stillWanted"/> says the wait is
    /// moot, or <see cref="MaxHeardWait"/> of playing has passed. A pause holds the
    /// wait without using up that time.
    /// </summary>
    private async Task WaitUntilHeardAsync(long frame, int generation, Func<bool> stillWanted)
    {
        var playing = TimeSpan.Zero;
        while (playing < MaxHeardWait && stillWanted() && _heard.Generation == generation)
        {
            var heard = HeardFrame();
            if (heard < 0 || heard >= frame)
                return;

            var step = TimeSpan.FromMilliseconds(50);
            if (State == PlaybackState.Playing)
            {
                var left = TimeSpan.FromSeconds((double)(frame - heard) / _heard.SampleRate);
                step = left < step ? left + TimeSpan.FromMilliseconds(1) : step;
                playing += step;
            }

            await Task.Delay(step);
        }
    }

    /// <summary>
    /// How far into the mixer's current block, in mixer bytes, the next track must
    /// start. BASSmix mixes a source added from the END sync from the start of the
    /// block it is working on, so without this the next track begins up to a block
    /// early, on top of <paramref name="finished"/>'s last frames. Harmless-sounding
    /// in PCM, but in DoP the sum destroys the markers and the DAC drops out of DSD
    /// for a moment: a gap between DSD tracks (session 51, measured on real DSFs).
    /// The frames <paramref name="finished"/> gave in this block are where it was
    /// before the pull to its end. Exact without resampling, where its frames are
    /// the mixer's. Resampled (shared mode, a track at another rate than Windows'),
    /// they are scaled to the mixer's rate, which is within the resampler's few
    /// frames of filter delay; DoP is never resampled. 0 (the old behaviour) when
    /// it can't be worked out.
    /// Caller holds <see cref="_gate"/>, on the device's thread.
    /// </summary>
    private long SpliceDelay(int finished)
    {
        if (_output is null || _pullStream != finished || _pullStartBytes < 0
            || !Bass.ChannelGetInfo(finished, out var info) || info.Frequency <= 0)
            return 0;

        var sourceFrames = (Bass.ChannelGetPosition(finished) - _pullStartBytes) / (sizeof(float) * info.Channels);
        var frames = info.Frequency == _output.MixerRate
            ? sourceFrames
            : sourceFrames * _output.MixerRate / info.Frequency;

        // A pull is a few hundredths of a second; more than a second is a stale reading.
        if (frames <= 0 || frames > _output.MixerRate)
            return 0;

        return frames * sizeof(float) * _output.MixerChannels;
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

                target = _order.Next(currentIndex, wrap: _repeat == RepeatMode.All);
                if (target < 0)
                    return;

                path = _queue[target].FilePath;
            }

            // File I/O outside the lock.
            var stream = OpenStream(path);
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
        _output.BeforePull = _notePullStart;
        _output.AfterPull = _notePullEnd;

        // A new connection starts with an empty buffer: what it is given is
        // counted afresh, at its rate. It hasn't pulled anything yet.
        _heard.Reset(_output.SampleRate);
        _tap.Reset(_output.SampleRate, _output.MixerChannels);
        return true;
    }

    /// <summary>
    /// Sets the playing stream, and the track it plays for the device's thread
    /// (<see cref="_playing"/>). Caller holds <see cref="_gate"/>, with
    /// <see cref="_index"/> already on the track.
    /// </summary>
    private void SetCurrentStream(int stream)
    {
        _currentStream = stream;
        Volatile.Write(ref _playing, stream != 0 && _index >= 0 && _index < _queue.Count
            ? new PlayingStream(stream, _queue[_index])
            : null);
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
    /// Bit-Perfect on means DSD goes out as DoP (session 51: no separate switch).
    /// DoP only means anything if the bits survive the trip to the DAC, so it is
    /// refused unless the output is exclusive (no resampling, unity gain) and the
    /// device can actually clock the DoP rate. Anything else and the markers would
    /// be mangled into noise, so falling back to PCM conversion is the right answer.
    /// </para>
    /// </summary>
    private int TryCreateDopStream(string path, BassFlags baseFlags)
    {
        if (_requestedMode != OutputMode.Exclusive)
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
            SetCurrentStream(0);
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
