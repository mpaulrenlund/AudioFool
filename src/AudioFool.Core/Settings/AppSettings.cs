using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFool.Core.Library;
using AudioFool.Core.Theme;

namespace AudioFool.Core.Settings;

/// <summary>
/// User settings, persisted as JSON under %APPDATA%\AudioFool, with the save
/// before it kept as settings.json.bak. Loading never throws: a corrupt file
/// falls back to the backup, and a missing one yields defaults.
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

    /// <summary>How often a locked file is tried again, and how long apart, before giving up on it.</summary>
    private const int ReadAttempts = 3;
    private const int ReadRetryMs = 100;

    /// <summary>Where <see cref="Save"/> writes: the file this was loaded from.</summary>
    private string _path = SettingsPath;

    /// <summary>
    /// True when settings.json is there but could be read neither as it is nor
    /// from its backup: this session runs on defaults (or the backup), and
    /// <see cref="Save"/> does nothing, so the real file is still there next time.
    /// Saving would have replaced the music folders and the Last.fm login with
    /// defaults, and the next scan would then drop every track in those folders.
    /// </summary>
    [JsonIgnore]
    public bool IsReadOnly { get; private set; }

    /// <summary>The copy of the previous save, kept by <see cref="Save"/>.</summary>
    public static string BackupPathFor(string path) => SafeJsonFile.BackupPathFor(path);

    public static AppSettings Load() => Load(SettingsPath, DateTime.UtcNow);

    /// <summary>
    /// Never throws. A file that is locked is tried again for a moment; one that
    /// can't be read is moved aside, like an unreadable playlists file, and the
    /// backup of the save before it is read instead. When there is a file but
    /// nothing usable can be read, the result is <see cref="IsReadOnly"/>.
    /// </summary>
    public static AppSettings Load(string path, DateTime nowUtc)
    {
        var main = Read(path);
        if (main.Settings is { } loaded)
            return loaded.WithDefaults(path);

        if (main.Broken)
            MoveAside(path, nowUtc);

        var backup = Read(BackupPathFor(path));
        if (backup.Settings is { } previous)
        {
            // From the backup: a locked file stays as it is, so nothing is saved
            // over it this session.
            previous.IsReadOnly = main.Locked;
            return previous.WithDefaults(path);
        }

        // No file at all is a first run, and an unreadable one has been moved aside
        // and kept. A locked one is still there, and must not be written over with
        // defaults.
        var defaults = new AppSettings { IsReadOnly = main.Locked };
        return defaults.WithDefaults(path);
    }

    /// <summary>
    /// Reads the file as it is on disk now, for changing one value in it: false when
    /// it can't be read, so the caller writes nothing rather than defaults. With no
    /// file yet, <paramref name="whenMissing"/> (the settings in use) is what gets
    /// changed and saved. No retries and no backup, since it is called while music
    /// plays.
    /// </summary>
    public static bool TryLoadCurrent(AppSettings whenMissing, out AppSettings settings) =>
        TryLoadCurrent(SettingsPath, whenMissing, out settings);

    public static bool TryLoadCurrent(string path, AppSettings whenMissing, out AppSettings settings)
    {
        var read = Read(path, attempts: 1);
        if (read.Settings is { } loaded)
        {
            settings = loaded.WithDefaults(path);
            return true;
        }

        var missing = !read.Locked && !read.Broken;
        settings = missing ? whenMissing : new AppSettings { _path = path, IsReadOnly = true };
        return missing;
    }

    private readonly record struct ReadResult(AppSettings? Settings, bool Locked, bool Broken);

    private static ReadResult Read(string path, int attempts = ReadAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return default;

                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);

                // "null" or a list where the settings should be: not readable as settings.
                return loaded?.MusicFolders is null
                    ? new ReadResult(null, Locked: false, Broken: true)
                    : new ReadResult(loaded, Locked: false, Broken: false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return new ReadResult(null, Locked: false, Broken: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held for a moment by a backup or a virus scan, most likely.
                if (attempt >= attempts)
                    return new ReadResult(null, Locked: true, Broken: false);

                Thread.Sleep(ReadRetryMs);
            }
        }
    }

    private static void MoveAside(string path, DateTime nowUtc)
    {
        try
        {
            File.Move(path, Path.ChangeExtension(path, $".unreadable-{nowUtc:yyyyMMddHHmmss}.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes a new file beside the old one, makes sure it is on the disk, and only
    /// then swaps it in, keeping the old one as the backup. A crash or a power cut
    /// mid-save leaves the old file, never a half-written one. Does nothing when
    /// <see cref="IsReadOnly"/>.
    /// </summary>
    public void Save()
    {
        if (IsReadOnly)
            return;

        try
        {
            SafeJsonFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Settings are a convenience; failing to save one shouldn't surface as an
            // error. The file on disk is still the last good one.
        }
    }

    /// <summary>
    /// First run has no configured folders, so start from the user's Music
    /// library - the app has something to show without any setup.
    /// </summary>
    private AppSettings WithDefaults(string path)
    {
        _path = path;
        DisabledFolders ??= [];

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
