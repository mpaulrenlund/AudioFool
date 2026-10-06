using ManagedBass;
using ManagedBass.Wasapi;

namespace AudioFool.Core.Playback;

/// <summary>
/// Owns one-time BASS initialisation and add-on loading.
/// <para>
/// BASS itself is a native library. The NuGet packages ship only the C# bindings,
/// so bass.dll, bassmix.dll and the format add-ons must sit next to the
/// executable. Missing add-ons are reported rather than thrown, so the app still
/// runs and can tell the user exactly which formats are unavailable.
/// </para>
/// </summary>
public sealed class BassRuntime
{
    /// <summary>Add-on DLL to the formats it enables, for the diagnostics message.</summary>
    private static readonly (string Dll, string Formats)[] OptionalPlugins =
    [
        ("bassflac.dll", "FLAC"),
        ("bassdsd.dll", "DSD (.dsf, .dff)"),
        ("bassopus.dll", "Opus"),
        ("bass_aac.dll", "AAC / M4A"),
        ("bassalac.dll", "ALAC"),
        ("basswma.dll", "WMA"),
        ("bassape.dll", "APE (Monkey's Audio)"),
        ("basswv.dll", "WavPack"),
        ("bass_mpc.dll", "Musepack"),
        ("bass_ac3.dll", "AC-3"),

        // TAK and DTS are deliberately absent: un4seen publishes no add-on for
        // either. Listing them here would make the status bar permanently report
        // missing add-ons that cannot be obtained. See README for the options.
    ];

    /// <summary>
    /// How long a change of default device is left to settle before it is probed.
    /// Windows announces one change per role, and a device that has just appeared
    /// can take a moment to answer format queries.
    /// </summary>
    private const int DeviceSettleMs = 500;

    private readonly List<int> _pluginHandles = [];
    private readonly object _probeGate = new();

    // Held in a field: BASSWASAPI calls it from its own thread.
    private WasapiNotifyProcedure? _notifyProc;
    private Timer? _deviceSettle;
    private string? _deviceId;

    public bool IsInitialised { get; private set; }

    /// <summary>
    /// Raised, on a worker thread, once the Windows default output device has
    /// changed and the new one has been probed.
    /// </summary>
    public event EventHandler? DefaultOutputChanged;

    /// <summary>
    /// The rate Windows mixes at for the default device. Shared-mode output runs
    /// here because that's the rate the system will resample to anyway.
    /// </summary>
    public int SharedMixRate { get; private set; } = 48000;

    /// <summary>Name of the default output device, for the status bar.</summary>
    public string OutputDeviceName { get; private set; } = "default device";

    /// <summary>Whether the default device will accept exclusive-mode access at all.</summary>
    public bool SupportsExclusive { get; private set; }

    /// <summary>Rates the default device accepts in exclusive mode, ascending.</summary>
    public IReadOnlyList<int> ExclusiveRates { get; private set; } = [];

    /// <summary>Add-ons that loaded, by the formats they enable.</summary>
    public IReadOnlyList<string> AvailableFormats { get; private set; } = [];

    /// <summary>Add-ons we looked for and didn't find.</summary>
    public IReadOnlyList<string> MissingFormats { get; private set; } = [];

    /// <summary>Set when BASS itself could not start.</summary>
    public string? InitError { get; private set; }

    public bool Initialise()
    {
        if (IsInitialised)
            return true;

        try
        {
            // Resampling quality, used only when shared mode forces a rate change.
            // Exclusive mode opens the device at the source rate and skips this.
            Bass.Configure(Configuration.SRCQuality, 4);

            // Device 0 is "no sound". BASS is only ever a decoder here - output goes
            // through WASAPI (see OutputChain). If BASS held the real endpoint open
            // in shared mode it would be competing with our own exclusive-mode
            // request for the same device.
            if (!Bass.Init(0, 48000, DeviceInitFlags.Default))
            {
                var error = Bass.LastError;

                // Re-initialising the same device is harmless, not a real failure.
                if (error != Errors.Already)
                {
                    InitError = $"BASS could not initialise ({error}).";
                    return false;
                }
            }

            LoadPlugins();
            ProbeOutputDevice();
            WatchDefaultDevice();

            IsInitialised = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            // bass.dll isn't beside the executable. The library still browses
            // fine without it, so report it and let the app carry on.
            InitError = "bass.dll was not found. Playback is unavailable until the " +
                        "BASS libraries are placed next to AudioFool.exe. " +
                        "See README.md for the one-time setup.";
            return false;
        }
        catch (BadImageFormatException)
        {
            InitError = "The BASS libraries beside AudioFool.exe are the wrong " +
                        "architecture. This is a 64-bit app, so it needs the x64 BASS builds.";
            return false;
        }
    }

    private void LoadPlugins()
    {
        var available = new List<string>();
        var missing = new List<string>();

        // Always present in bass.dll itself.
        available.Add("MP3");
        available.Add("Ogg Vorbis");
        available.Add("WAV / AIFF");
        available.Add("MOD / S3M / XM / IT");

        // Resolve against the executable's own folder. A bare filename is resolved
        // against the working directory instead, which is wherever the app happened
        // to be launched from - a desktop shortcut can set that to anything.
        var pluginFolder = AppContext.BaseDirectory;

        foreach (var (dll, formats) in OptionalPlugins)
        {
            var handle = 0;
            try
            {
                // Returns 0 when the file simply isn't there, which is the normal
                // case for add-ons the user chose not to install.
                handle = Bass.PluginLoad(Path.Combine(pluginFolder, dll));
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                // A 32-bit or damaged add-on: treat it as absent.
            }

            if (handle != 0)
            {
                _pluginHandles.Add(handle);
                available.Add(formats);
            }
            else
            {
                missing.Add(formats);
            }
        }

        AvailableFormats = available;
        MissingFormats = missing;
    }

    /// <summary>
    /// Asks BASSWASAPI to report device changes. Only a new default output
    /// matters: output always opens the default device (see OutputChain).
    /// </summary>
    private void WatchDefaultDevice()
    {
        try
        {
            _notifyProc = OnWasapiNotify;
            BassWasapi.SetNotify(_notifyProc);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            // basswasapi.dll missing: there is no device to follow.
        }
    }

    private void OnWasapiNotify(WasapiNotificationType notify, int device, IntPtr user)
    {
        if (notify != WasapiNotificationType.DefaultOutput)
            return;

        lock (_probeGate)
        {
            _deviceSettle ??= new Timer(_ => ReprobeDefaultDevice());
            _deviceSettle.Change(DeviceSettleMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Probes the default device again, and raises <see cref="DefaultOutputChanged"/>
    /// if it is a different one. Public so a test can stand in for the notification.
    /// </summary>
    public bool ReprobeDefaultDevice()
    {
        bool changed;
        lock (_probeGate)
        {
            var before = _deviceId;
            ProbeOutputDevice();
            changed = _deviceId != before;
        }

        if (changed)
            DefaultOutputChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>
    /// Asks the default output device what it can do, so the UI can offer
    /// exclusive mode only when it's actually achievable. Run again whenever
    /// the default device changes.
    /// </summary>
    private void ProbeOutputDevice()
    {
        // Both rate families and their multiples. The top end matters even though
        // no music file is 768 kHz: DSD128 converts to PCM at 705.6 kHz (an eighth
        // of its rate), and DoP for DSD256 needs 705.6 kHz too.
        int[] candidates =
        [
            44100, 48000, 88200, 96000, 176400, 192000,
            352800, 384000, 705600, 768000,
        ];

        try
        {
            for (var i = 0; BassWasapi.GetDeviceInfo(i, out var info); i++)
            {
                if (info.IsInput || info.IsLoopback || !info.IsDefault)
                    continue;

                // Still the same device (Windows announces each role separately).
                // Probing it again could even be wrong: a device we hold in
                // exclusive mode refuses format checks.
                if (_deviceId is not null && info.ID == _deviceId)
                    return;

                _deviceId = info.ID;
                OutputDeviceName = info.Name ?? "default device";
                if (info.MixFrequency > 0)
                    SharedMixRate = info.MixFrequency;

                ExclusiveRates =
                [
                    .. candidates.Where(rate =>
                        BassWasapi.CheckFormat(i, rate, 2, WasapiInitFlags.Exclusive) != WasapiFormat.Unknown)
                ];

                SupportsExclusive = ExclusiveRates.Count > 0;
                ConfigureDsdConversionRate();
                return;
            }

            // No default output at all, e.g. the last device was unplugged.
            _deviceId = null;
            ExclusiveRates = [];
            SupportsExclusive = false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            // basswasapi.dll missing: shared output still works, exclusive doesn't.
            SupportsExclusive = false;
        }
    }

    /// <summary>
    /// DSD has to be converted to PCM for any DAC that won't take it natively, and
    /// BASSDSD defaults to 88.2 kHz. That throws away resolution a modern DAC can
    /// happily accept, so pick the highest 44.1-family rate the device supports.
    /// <para>
    /// The 44.1 family matters: DSD is clocked at a multiple of 44,100, so
    /// converting to 48 kHz would add a resampling step this whole path exists
    /// to avoid.
    /// </para>
    /// </summary>
    private void ConfigureDsdConversionRate()
    {
        // BASSDSD only accepts 1/8, 1/16, 1/32... of the DSD rate, so these are the
        // reachable values; it rounds to a valid one for whatever the file's DSD
        // rate turns out to be. 705.6 kHz is an eighth of DSD128.
        int[] preferred = [705600, 352800, 176400, 88200];

        // None of them (no exclusive mode at all): BASSDSD's own default, so a
        // device switched to doesn't keep the last one's rate.
        var best = preferred.FirstOrDefault(ExclusiveRates.Contains);
        if (best == 0)
            best = 88200;

        Bass.Configure(Configuration.DSDFrequency, best);
        DsdConversionRate = best;
    }

    /// <summary>
    /// Whether the device can clock DoP for a given DSD rate. DoP carries the
    /// bitstream at one sixteenth of the DSD rate.
    /// </summary>
    public bool SupportsDopFor(int dsdRate) =>
        dsdRate > 0 && ExclusiveRates.Contains(dsdRate / 16);

    /// <summary>True when the device can carry DoP for at least DSD64.</summary>
    public bool SupportsDop => ExclusiveRates.Contains(176400);

    /// <summary>Rate DSD is decoded to when the DAC can't take DSD directly.</summary>
    public int DsdConversionRate { get; private set; } = 88200;

    public void Shutdown()
    {
        if (!IsInitialised)
            return;

        if (_notifyProc is not null)
            BassWasapi.SetNotify(null);
        _deviceSettle?.Dispose();

        foreach (var handle in _pluginHandles)
            Bass.PluginFree(handle);

        _pluginHandles.Clear();
        Bass.Free();
        IsInitialised = false;
    }
}
