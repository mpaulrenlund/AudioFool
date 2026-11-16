using System.IO;
using AudioFool.Core.Equalizer;
using AudioFool.Core.Settings;
using Microsoft.Win32;

namespace AudioFool.ViewModels;

/// <summary>
/// The equalizer (session 79, the user's design): the status bar's EQ chip opens
/// a menu of presets, Peace's (read in place, never written) and AudioFool's own
/// folder's. The choice is remembered across launches. The engine does the rest
/// (<see cref="Core.Playback.AudioEngine.Equalizer"/>).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The presets the EQ menu offers, Peace's and AudioFool's own folder's.</summary>
    public sealed record EqualizerMenu(IReadOnlyList<EqPreset> Peace, IReadOnlyList<EqPreset> Own);

    private EqPreset? _equalizerPreset;
    private string? _equalizerFolder;

    /// <summary>
    /// Equalizer APO's config folder, where Peace keeps its presets: from the
    /// registry, as Equalizer APO's installer writes it; null when it isn't installed.
    /// </summary>
    private static readonly Lazy<string?> PeaceFolder = new(FindPeaceFolder);

    private static string? FindPeaceFolder()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            if (key?.GetValue("ConfigPath") is string config && Directory.Exists(config))
                return config;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config");
        return Directory.Exists(standard) ? standard : null;
    }

    /// <summary>The chip's dot: lit while a preset is in use.</summary>
    public bool IsEqualizerOn => _equalizerPreset is not null;

    private EqGraph? _equalizerGraph;

    /// <summary>
    /// What the album header's EQ panel draws (session 80): the preset in use,
    /// worked out once each time it changes; null while the EQ is off.
    /// </summary>
    public EqGraph? EqualizerGraph =>
        _equalizerPreset is not { } preset ? null
        : _equalizerGraph is { } graph && ReferenceEquals(graph.Preset, preset) ? graph
        : _equalizerGraph = EqGraph.For(preset);

    /// <summary>
    /// Whether a click on the EQ panel opens Peace: a Peace preset in use and Peace
    /// installed. AudioFool's own folder's presets are files Peace can't open.
    /// </summary>
    public bool CanOpenEqualizerInPeace =>
        _equalizerPreset is not null
        && _equalizerFolder == AppSettings.EqualizerInPeace
        && PeaceExecutable is not null;

    private void OnEqualizerChanged()
    {
        OnPropertyChanged(nameof(IsEqualizerOn));
        OnPropertyChanged(nameof(EqualizerAutomationName));
        OnPropertyChanged(nameof(EqualizerGraph));
        OnPropertyChanged(nameof(CanOpenEqualizerInPeace));
    }

    public string EqualizerAutomationName =>
        _equalizerPreset is { } preset ? $"Equalizer: {preset.Name}" : "Equalizer: off";

    /// <summary>
    /// Read afresh each time the menu opens, so a file dropped in or a preset
    /// changed in Peace shows at once. Only presets AudioFool plays and that change
    /// something: one using filters it doesn't run, or a flat one, isn't offered.
    /// </summary>
    public EqualizerMenu LoadEqualizerMenu()
    {
        static IReadOnlyList<EqPreset> Offered(IReadOnlyList<EqPreset> presets) =>
            [.. presets.Where(p => p.IsSupported && !p.IsFlat)];

        return new EqualizerMenu(
            Offered(EqPresetLibrary.Load(PeaceFolder.Value, null)),
            Offered(EqPresetLibrary.Load(null, AppSettings.EqualizerDirectory)));
    }

    /// <summary>
    /// Takes up an edit to the preset in use, found in <paramref name="menu"/> (a
    /// fresh read): it plays from the next block, crossfaded like any change. Called
    /// as the menu opens, so an edit made in Peace is heard without picking the preset
    /// again. A preset no longer offered (deleted, made flat, or using something
    /// AudioFool doesn't run) leaves the one in use playing as it was.
    /// </summary>
    public void TakeUpEqualizerEdit(EqualizerMenu menu)
    {
        if (_equalizerPreset is not { } current)
            return;

        var presets = _equalizerFolder == AppSettings.EqualizerInOwnFolder ? menu.Own : menu.Peace;
        var fresh = presets.FirstOrDefault(p => string.Equals(p.Name, current.Name, StringComparison.OrdinalIgnoreCase));
        TakeUp(current, fresh);
    }

    /// <summary>
    /// Reads the preset in use from its file again and takes up an edit, as the menu
    /// does as it opens. Called when AudioFool's window is switched back to (session
    /// 80), so a change saved in Peace shows on the EQ panel, and is heard, without
    /// opening the menu. One small file; a file gone, unreadable, made flat or using
    /// something AudioFool doesn't run leaves the preset playing as it was.
    /// </summary>
    public void TakeUpEqualizerEditFromFile()
    {
        if (_equalizerPreset is not { SourcePath: { Length: > 0 } path } current)
            return;

        EqPreset fresh;
        try
        {
            if (!File.Exists(path))
                return;
            fresh = current.Format == EqPresetFormat.Peace
                ? PeacePresetReader.Read(path)
                : EqualizerApoTextReader.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // being saved this moment, most likely: the next switch back reads it
        }

        if (fresh.IsSupported && !fresh.IsFlat)
            TakeUp(current, fresh);
    }

    private void TakeUp(EqPreset current, EqPreset? fresh)
    {
        if (fresh is null || fresh.SameAs(current))
            return;

        _engine.Equalizer = fresh;
        _equalizerPreset = fresh;
        OnEqualizerChanged();
    }

    /// <summary>
    /// Peace, Equalizer APO's preset editor, in its config folder (the 64-bit one when
    /// it's there); null when Peace isn't installed, and the menu leaves Open Peace out.
    /// </summary>
    public static string? PeaceExecutable =>
        PeaceFolder.Value is { } folder
            ? new[] { "Peace64.exe", "Peace.exe" }.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists)
            : null;

    /// <summary>Starts Peace to edit or add presets. It runs as the user (no elevation asked).</summary>
    public void OpenPeace()
    {
        if (PeaceExecutable is not { } peace)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(peace)
            {
                WorkingDirectory = Path.GetDirectoryName(peace)!,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            StatusText = $"Couldn't open Peace: {ex.Message}";
        }
    }

    /// <summary>Whether <paramref name="preset"/> from <paramref name="folder"/> is the one in use.</summary>
    public bool IsEqualizerInUse(EqPreset preset, string folder) =>
        _equalizerPreset is { } current
        && string.Equals(_equalizerFolder, folder, StringComparison.Ordinal)
        && string.Equals(current.Name, preset.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Plays through <paramref name="preset"/> from <paramref name="folder"/> (one of
    /// <see cref="AppSettings.EqualizerInPeace"/> and <see cref="AppSettings.EqualizerInOwnFolder"/>),
    /// or none, from the next block on; saved at once.
    /// </summary>
    public void ChooseEqualizer(EqPreset? preset, string? folder)
    {
        _engine.Equalizer = preset;
        _equalizerPreset = preset;
        _equalizerFolder = preset is null ? null : folder;

        _settings.SetEqualizer(preset?.Name, _equalizerFolder);
        _settings.Save();

        OnEqualizerChanged();
        OutputDescription = _engine.OutputDescription;
    }

    /// <summary>
    /// A double-click on the EQ chip (session 80, the user's request): on turns it
    /// off; off turns the last preset used back on, read afresh from its folder. With
    /// none used yet nothing happens; with its file gone, or no longer one AudioFool
    /// plays, the status line says so and the equalizer stays off.
    /// </summary>
    public bool CanToggleEqualizer => _equalizerPreset is not null || _settings.LastEqualizer is not null;

    public void ToggleEqualizer()
    {
        if (_equalizerPreset is not null)
        {
            ChooseEqualizer(null, null);
            return;
        }

        if (_settings.LastEqualizer is not { } last)
            return;

        var menu = LoadEqualizerMenu();
        var presets = last.Folder == AppSettings.EqualizerInOwnFolder ? menu.Own : menu.Peace;
        if (presets.FirstOrDefault(p => string.Equals(p.Name, last.Preset, StringComparison.OrdinalIgnoreCase)) is { } preset)
            ChooseEqualizer(preset, last.Folder);
        else
            StatusText = $"Couldn't find the EQ preset {last.Preset}";
    }

    /// <summary>
    /// The preset remembered from last time, if its file is still there and still
    /// one AudioFool plays; otherwise the equalizer starts off and the setting is
    /// left as it is.
    /// </summary>
    private void RestoreEqualizer()
    {
        if (_settings.EqualizerPreset is not { Length: > 0 } name)
            return;

        var menu = LoadEqualizerMenu();
        var (presets, folder) = _settings.EqualizerFolder == AppSettings.EqualizerInOwnFolder
            ? (menu.Own, AppSettings.EqualizerInOwnFolder)
            : (menu.Peace, AppSettings.EqualizerInPeace);

        if (presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } preset)
            return;

        _engine.Equalizer = preset;
        _equalizerPreset = preset;
        _equalizerFolder = folder;
        OnEqualizerChanged();
    }

    /// <summary>Opens AudioFool's own preset folder in Explorer, making it first if need be.</summary>
    public void OpenEqualizerFolder()
    {
        var folder = AppSettings.EqualizerDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Couldn't open {folder}: {ex.Message}";
        }
    }
}
