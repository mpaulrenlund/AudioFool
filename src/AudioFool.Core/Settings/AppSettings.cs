using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFool.Core.Theme;

namespace AudioFool.Core.Settings;

/// <summary>
/// User settings, persisted as JSON under %APPDATA%\AudioFool.
/// Loading never throws: a missing or corrupt file just yields defaults.
/// </summary>
public sealed class AppSettings
{
    public List<string> MusicFolders { get; set; } = [];

    /// <summary>Folders that are present in the library but hidden from the current view.</summary>
    public List<string> DisabledFolders { get; set; } = [];

    public double Volume { get; set; } = 1.0;

    /// <summary>
    /// Where the main window was when it last closed; it opens there again when
    /// that is still on a screen. Null until the window has been closed once.
    /// </summary>
    public WindowBounds? Window { get; set; }

    /// <summary>
    /// The track that was playing most recently, so the next launch can open on it.
    /// Null until something has played.
    /// </summary>
    public LastPlayedTrack? LastPlayed { get; set; }

    /// <summary>Rescan the library on startup rather than waiting to be asked.</summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>
    /// System-wide F9 / F10 / F11 playback keys. On by default, but worth knowing
    /// that Windows gives a hotkey exclusively to whoever claims it first - so while
    /// this is on, those keys stop reaching other applications. Set it to false here
    /// if that conflicts with something you use.
    /// </summary>
    public bool GlobalHotkeys { get; set; } = true;

    /// <summary>
    /// Shared by default. Exclusive mode silences every other application on the
    /// machine, which isn't something to opt someone into without them asking.
    /// Stored by name so the file stays readable if the enum ever gains members.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<Playback.OutputMode>))]
    public Playback.OutputMode OutputMode { get; set; } = Playback.OutputMode.Shared;

    /// <summary>Play the queue in a shuffled order.</summary>
    public bool Shuffle { get; set; }

    /// <summary>
    /// What happens at the end of the queue. Stored by name, like the other enums
    /// here, so the file stays readable.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<Playback.RepeatMode>))]
    public Playback.RepeatMode Repeat { get; set; } = Playback.RepeatMode.Off;

    /// <summary>
    /// Light or Dark, saved whenever it is switched and applied before the main
    /// window is built. Stored by name, like the other enums here, but read by
    /// <see cref="ThemeSettingConverter"/>, which never throws.
    /// </summary>
    [JsonConverter(typeof(ThemeSettingConverter))]
    public AppTheme Theme { get; set; } = DefaultTheme;

    /// <summary>
    /// When the file has no theme: the first run since the dark theme arrived, or
    /// a new settings file (PS2 Dark spec 3.2).
    /// </summary>
    public const AppTheme DefaultTheme = AppTheme.Dark;

    /// <summary>
    /// How lively the visualizer is, from 1 (calm) to 10; Up and Down change it in
    /// its window (<see cref="Analysis.LiveSpectrum.Intensity"/>). A number outside
    /// that range in the file is brought into it.
    /// </summary>
    public int VisualizerIntensity
    {
        get => _visualizerIntensity;
        set => _visualizerIntensity = Math.Clamp(value, Analysis.LiveSpectrum.LowestIntensity, Analysis.LiveSpectrum.HighestIntensity);
    }

    private int _visualizerIntensity = Analysis.LiveSpectrum.DefaultIntensity;

    /// <summary>
    /// Where the visualizer window was when it last closed (as a window, not
    /// fullscreen); null until it has been closed once.
    /// </summary>
    public WindowBounds? VisualizerWindow { get; set; }

    /// <summary>
    /// A personal API key from fanart.tv, for the tag dialog's "Search Internet".
    /// Without one that search uses the iTunes Store and the Cover Art Archive
    /// only. There is deliberately no UI for it: it is set once, by hand, here.
    /// </summary>
    public string? FanartTvApiKey { get; set; }

    /// <summary>
    /// The user's own Last.fm API account (Last.fm issues one per application),
    /// entered in the Last.fm window. Kept after disconnecting so reconnecting
    /// is one click.
    /// </summary>
    public string? LastFmApiKey { get; set; }

    public string? LastFmApiSecret { get; set; }

    /// <summary>
    /// The session from the browser approval. It does not expire; the user can
    /// revoke it on last.fm. It grants scrobbling, not the password.
    /// </summary>
    public string? LastFmSessionKey { get; set; }

    public string? LastFmUserName { get; set; }

    /// <summary>Scrobbling while connected. Off pauses it without disconnecting.</summary>
    public bool LastFmScrobbling { get; set; } = true;

    [JsonIgnore]
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AudioFool");

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded.WithDefaults();
            }
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or JsonException
                                     or NotSupportedException)
        {
            // Fall through to defaults rather than refusing to start.
        }

        return new AppSettings().WithDefaults();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Settings are a convenience; failing to save one shouldn't surface as an error.
        }
    }

    /// <summary>
    /// First run has no configured folders, so start from the user's Music
    /// library - the app has something to show without any setup.
    /// </summary>
    private AppSettings WithDefaults()
    {
        if (MusicFolders.Count == 0)
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (!string.IsNullOrEmpty(music) && Directory.Exists(music))
                MusicFolders.Add(music);
        }

        Volume = Math.Clamp(Volume, 0, 1);
        return this;
    }
}

/// <summary>
/// Where to open the library next time: the track's path, plus the artist (as the
/// sidebar names them) and album, which still find it if the drive has changed
/// letter since.
/// </summary>
public sealed record LastPlayedTrack(string FilePath, string Artist, string Album);

/// <summary>
/// Reads <see cref="AppSettings.Theme"/> without ever throwing. Until session 18 the
/// app saved a "Theme" of its own ("Dark", "Vista", "PS1"), and an old settings.json
/// can still carry one. A strict enum reader throws on "Vista", and
/// <see cref="AppSettings.Load"/> would then start from defaults: the music folders
/// and the Last.fm session would be gone at the next save. So anything other than
/// "Light" or "Dark" - another name, a number, null, an object - reads as
/// <see cref="AppSettings.DefaultTheme"/>.
/// </summary>
internal sealed class ThemeSettingConverter : JsonConverter<AppTheme>
{
    public override bool HandleNull => true;

    public override AppTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

        // Past an object or an array; nothing to do for anything else.
        reader.Skip();

        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            if (string.Equals(name, theme.ToString(), StringComparison.OrdinalIgnoreCase))
                return theme;
        }

        return AppSettings.DefaultTheme;
    }

    public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
