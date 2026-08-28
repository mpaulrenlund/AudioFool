using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// PCM conversion by default: DoP only works on a DAC that speaks it, and the
    /// conversion path is correct everywhere.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<Playback.DsdMode>))]
    public Playback.DsdMode DsdMode { get; set; } = Playback.DsdMode.ConvertToPcm;

    public string Theme { get; set; } = "Dark";

    /// <summary>Play the queue in a shuffled order.</summary>
    public bool Shuffle { get; set; }

    /// <summary>
    /// What happens at the end of the queue. Stored by name, like the other enums
    /// here, so the file stays readable.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<Playback.RepeatMode>))]
    public Playback.RepeatMode Repeat { get; set; } = Playback.RepeatMode.Off;

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
