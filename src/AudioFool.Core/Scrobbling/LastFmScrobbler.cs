using AudioFool.Core.Models;

namespace AudioFool.Core.Scrobbling;

/// <summary>
/// Turns playback into Last.fm scrobbles: feeds <see cref="PlayTracker"/>, sends
/// "now playing" when a play starts, queues each scrobble on disk the moment it
/// is earned, and sends the queue in batches.
/// <para>
/// Scrobbling at the threshold rather than at the end of the track means a
/// play survives the app being closed halfway through the second half.
/// </para>
/// <para>
/// A failed send leaves the queue alone and backs off, doubling from one minute
/// to half an hour; the next position report after that retries. A rejected
/// session stops sending altogether until the user reconnects - the queue is
/// kept for when they do.
/// </para>
/// <para>
/// Called from one thread (the UI's). Sends run in the background and never
/// throw back into playback.
/// </para>
/// </summary>
public sealed class LastFmScrobbler
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private readonly PlayTracker _tracker = new();
    private readonly ScrobbleQueue _queue;
    private readonly TimeProvider _clock;
    private readonly Func<string, string, LastFmApi> _apiFactory;

    private LastFmApi? _api;
    private LastFmSession? _session;
    private bool _flushing;
    private DateTimeOffset _retryAt;
    private TimeSpan _backoff = FirstBackoff;

    public LastFmScrobbler(ScrobbleQueue queue, TimeProvider? clock = null, Func<string, string, LastFmApi>? apiFactory = null)
    {
        _queue = queue;
        _clock = clock ?? TimeProvider.System;
        _apiFactory = apiFactory ?? ((key, secret) => new LastFmApi(key, secret));
    }

    /// <summary>Raised on the calling thread's context whenever <see cref="Status"/> may have changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// Raised when Last.fm rejects the session or key, with a message for the
    /// status bar. Once per failure, not once per retry.
    /// </summary>
    public event EventHandler<string>? AuthFailed;

    public bool Enabled { get; set; } = true;

    public bool IsConnected => _session is not null;

    public string? UserName => _session?.UserName;

    /// <summary>True after Last.fm rejected the credentials, until <see cref="Connect"/>.</summary>
    public bool NeedsReconnect { get; private set; }

    public int Pending => _queue.Count;

    /// <summary>The last send's error, or null when it went through.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset? LastSentAt { get; private set; }

    public void Connect(string apiKey, string secret, LastFmSession session)
    {
        _api = _apiFactory(apiKey, secret);
        _session = session;
        NeedsReconnect = false;
        LastError = null;
        _retryAt = default;
        _backoff = FirstBackoff;
        OnStatusChanged();
        _ = FlushAsync();
    }

    public void Disconnect()
    {
        _api = null;
        _session = null;
        NeedsReconnect = false;
        LastError = null;
        OnStatusChanged();
    }

    /// <summary>A track began playing from the start.</summary>
    public void TrackStarted(Track track, TimeSpan duration)
    {
        if (_tracker.Start(track, duration, _clock.GetUtcNow()))
            SendNowPlaying();
    }

    public void Stopped() => _tracker.Stop();

    /// <summary>
    /// The playing position, reported a few times a second while playing, with
    /// the track the engine says is playing now. A gapless handover swaps the
    /// stream on the mixer thread and only then posts the track change, so for
    /// a tick or two the position is the new track's while the tracker still
    /// holds the old one; those ticks are skipped rather than read as the old
    /// track jumping back to its start.
    /// </summary>
    public void Advance(TimeSpan position, Track? playing = null)
    {
        if (playing is not null && _tracker.Current is { } current
            && !string.Equals(playing.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        var now = _clock.GetUtcNow();
        var result = _tracker.Advance(position, now);

        if (result.Restarted)
            SendNowPlaying();

        // Earned while switched off or disconnected is still not queued: the
        // switch means "don't record my plays", not "record them for later".
        if (result.Due is { } due && Enabled && IsConnected)
        {
            _queue.Add(due);
            OnStatusChanged();
            _ = FlushAsync();
        }
        else if (_queue.Count > 0 && now >= _retryAt)
        {
            _ = FlushAsync();
        }
    }

    private void SendNowPlaying()
    {
        if (!Enabled || _api is not { } api || _session is not { } session || NeedsReconnect)
            return;
        if (_tracker.NowPlaying() is not { } entry)
            return;

        _ = SendNowPlayingAsync(api, session, entry);
    }

    private async Task SendNowPlayingAsync(LastFmApi api, LastFmSession session, ScrobbleEntry entry)
    {
        try
        {
            await api.UpdateNowPlayingAsync(session.Key, entry, CancellationToken.None);
        }
        catch (LastFmException ex) when (ex.IsAuthFailure)
        {
            RejectSession(ex);
        }
        catch (LastFmException)
        {
            // "Now playing" is a courtesy; it is not retried or queued.
        }
    }

    /// <summary>
    /// Sends the queue, oldest first, a batch at a time, until it is empty or a
    /// batch fails. Only one flush runs at once.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_flushing || !Enabled || _api is not { } api || _session is not { } session || NeedsReconnect)
            return;

        _flushing = true;
        try
        {
            while (_queue.Peek(LastFmApi.MaxBatch) is { Count: > 0 } batch)
            {
                IReadOnlyList<ScrobbleOutcome> outcomes;
                try
                {
                    outcomes = await api.ScrobbleAsync(session.Key, batch, CancellationToken.None);
                }
                catch (LastFmException ex) when (ex.IsAuthFailure)
                {
                    RejectSession(ex);
                    return;
                }
                catch (LastFmException ex)
                {
                    LastError = ex.Message;
                    _retryAt = _clock.GetUtcNow() + _backoff;
                    _backoff = _backoff * 2 < MaxBackoff ? _backoff * 2 : MaxBackoff;
                    return;
                }

                // Deferred (the daily limit) stays queued; everything else is done.
                var done = batch.Where((_, i) => outcomes[i] != ScrobbleOutcome.Deferred).ToList();
                _queue.Remove(done);

                LastError = null;
                LastSentAt = _clock.GetUtcNow();
                _backoff = FirstBackoff;

                if (done.Count < batch.Count)
                {
                    _retryAt = _clock.GetUtcNow() + MaxBackoff;
                    return;
                }
            }
        }
        finally
        {
            _flushing = false;
            OnStatusChanged();
        }
    }

    private void RejectSession(LastFmException ex)
    {
        if (NeedsReconnect)
            return;

        NeedsReconnect = true;
        LastError = ex.Message;
        OnStatusChanged();
        AuthFailed?.Invoke(this, $"Last.fm stopped accepting scrobbles ({ex.Message}). Reconnect from the menu.");
    }

    private void OnStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
}
