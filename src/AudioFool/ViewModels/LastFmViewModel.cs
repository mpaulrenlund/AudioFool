using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Scrobbling;
using AudioFool.Core.Settings;

namespace AudioFool.ViewModels;

/// <summary>
/// Backs <see cref="LastFmWindow"/>: connecting with the user's own API key and
/// secret, the scrobbling switch, and what the scrobbler is doing.
/// <para>
/// Connecting is Last.fm's desktop flow. A token is fetched, last.fm opens in
/// the user's browser to approve it, and this polls auth.getSession until the
/// approval lands - so there is no "I've approved it" button to forget, and the
/// password is only ever typed into last.fm itself.
/// </para>
/// </summary>
public sealed partial class LastFmViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollLimit = TimeSpan.FromMinutes(5);

    private readonly LastFmScrobbler _scrobbler;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _connectCts;
    private string? _authorizeUrl;
    private bool _cancelledByUser;

    public LastFmViewModel(LastFmScrobbler scrobbler, AppSettings settings)
    {
        _scrobbler = scrobbler;
        _settings = settings;

        _apiKey = settings.LastFmApiKey ?? "";
        _apiSecret = settings.LastFmApiSecret ?? "";
        _scrobblingEnabled = scrobbler.Enabled;

        _scrobbler.StatusChanged += OnScrobblerStatusChanged;

        if (scrobbler.NeedsReconnect)
            _error = $"Last.fm rejected the saved session ({scrobbler.LastError}). Connect again to resume scrobbling.";
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _apiKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _apiSecret;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSetup), nameof(ShowsConnected))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isWaitingForApproval;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _scrobblingEnabled;

    /// <summary>Key and secret boxes: before connecting, and again once Last.fm rejects the session.</summary>
    public bool ShowsSetup => !_scrobbler.IsConnected || _scrobbler.NeedsReconnect;

    public bool ShowsConnected => !ShowsSetup;

    public string ConnectedAs => $"Connected as {_scrobbler.UserName}";

    public string ProfileUrl => $"https://www.last.fm/user/{Uri.EscapeDataString(_scrobbler.UserName ?? "")}";

    public string QueueStatus
    {
        get
        {
            var pending = _scrobbler.Pending switch
            {
                0 => "Nothing waiting to send.",
                1 => "1 scrobble waiting to send.",
                var n => $"{n:N0} scrobbles waiting to send.",
            };

            if (_scrobbler.LastError is { } error)
                return $"{pending} Last send failed: {error} It will be retried.";

            return _scrobbler.LastSentAt is { } sent
                ? $"{pending} Last sent {sent.ToLocalTime():HH:mm}."
                : pending;
        }
    }

    partial void OnScrobblingEnabledChanged(bool value)
    {
        _scrobbler.Enabled = value;
        _settings.LastFmScrobbling = value;
        _settings.Save();

        if (value)
            _ = _scrobbler.FlushAsync();
    }

    private bool CanConnect() =>
        !IsWaitingForApproval && ApiKey.Trim().Length > 0 && ApiSecret.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var key = ApiKey.Trim();
        var secret = ApiSecret.Trim();
        var api = new LastFmApi(key, secret);

        Error = null;
        IsWaitingForApproval = true;
        _connectCts = new CancellationTokenSource(PollLimit);
        var ct = _connectCts.Token;

        try
        {
            var token = await api.GetTokenAsync(ct);

            // The key and secret evidently work, so keep them even if the
            // approval is never given.
            _settings.LastFmApiKey = key;
            _settings.LastFmApiSecret = secret;
            _settings.Save();

            _authorizeUrl = LastFmApi.AuthorizeUrl(key, token);
            OpenInBrowser(_authorizeUrl);

            while (true)
            {
                await Task.Delay(PollInterval, ct);
                try
                {
                    var session = await api.GetSessionAsync(token, ct);

                    _settings.LastFmSessionKey = session.Key;
                    _settings.LastFmUserName = session.UserName;
                    _settings.Save();

                    _scrobbler.Connect(key, secret, session);
                    return;
                }
                catch (LastFmException ex) when (ex.IsTokenNotYetAuthorised || ex.IsTransient)
                {
                    // Still waiting for the user, or a blip: ask again.
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_connectCts?.IsCancellationRequested == true && !_cancelledByUser)
                Error = "Last.fm wasn't approved within five minutes. Click Connect to try again.";
        }
        catch (LastFmException ex)
        {
            Error = ex.Code switch
            {
                10 => "Last.fm doesn't recognise that API key.",
                13 => "The shared secret doesn't match that API key.",
                15 => "The approval expired. Click Connect to try again.",
                _ => ex.Message,
            };
        }
        finally
        {
            _cancelledByUser = false;
            _connectCts?.Dispose();
            _connectCts = null;
            _authorizeUrl = null;
            IsWaitingForApproval = false;
        }
    }

    [RelayCommand]
    private void CancelConnect()
    {
        _cancelledByUser = true;
        _connectCts?.Cancel();
    }

    [RelayCommand]
    private void OpenApprovalPageAgain()
    {
        if (_authorizeUrl is not null)
            OpenInBrowser(_authorizeUrl);
    }

    [RelayCommand]
    private void Disconnect()
    {
        _settings.LastFmSessionKey = null;
        _settings.LastFmUserName = null;
        _settings.Save();
        _scrobbler.Disconnect();
    }

    [RelayCommand]
    private void OpenLink(string url) => OpenInBrowser(url);

    public string CreateApiAccountUrl => LastFmApi.CreateApiAccountUrl;

    public string ApplicationsSettingsUrl => LastFmApi.ApplicationsSettingsUrl;

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser; the approval page link stays available.
        }
    }

    private void OnScrobblerStatusChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ShowsSetup));
        OnPropertyChanged(nameof(ShowsConnected));
        OnPropertyChanged(nameof(ConnectedAs));
        OnPropertyChanged(nameof(ProfileUrl));
        OnPropertyChanged(nameof(QueueStatus));
    }

    public void Dispose()
    {
        CancelConnect();
        _scrobbler.StatusChanged -= OnScrobblerStatusChanged;
    }
}
