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

    // Held in a field for the lifetime of the chain: BASS calls this from its own
    // thread, and a collected delegate is an instant crash.
    private readonly WasapiProcedure? _wasapiProc;

    private int _mixer;
    private bool _disposed;

    private OutputChain(int mixer, int sampleRate, int channels, OutputMode mode, WasapiProcedure? proc)
    {
        _mixer = mixer;
        _wasapiProc = proc;
        SampleRate = sampleRate;
        Channels = channels;
        Mode = mode;
    }

    public int Mixer => _mixer;
    public int SampleRate { get; }
    public int Channels { get; }
    public OutputMode Mode { get; }

    /// <summary>The bit depth WASAPI actually opened, for display. 0 when unknown.</summary>
    public int DeviceBitDepth { get; private init; }

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

        // The callback simply drains the mixer. Returning less than requested would
        // stall the device, which MixerNonStop prevents by emitting silence when no
        // source is attached.
        WasapiProcedure proc = (buffer, length, _) => Bass.ChannelGetData(mixer, buffer, length);

        var initFlags = mode == OutputMode.Exclusive
            ? WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven | WasapiInitFlags.Buffer
            : WasapiInitFlags.Shared | WasapiInitFlags.Buffer;

        if (!BassWasapi.Init(-1, sampleRate, channels, initFlags, BufferSeconds, 0f, proc))
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
            sampleRate = info.Frequency > 0 ? info.Frequency : sampleRate;
            channels = info.Channels > 0 ? info.Channels : channels;
        }

        return new OutputChain(mixer, sampleRate, channels, mode, proc) { DeviceBitDepth = depth };
    }

    public bool Start() => BassWasapi.Start();

    /// <summary>Stops the device without discarding what's buffered.</summary>
    public void Pause() => BassWasapi.Stop(false);

    public void Resume() => BassWasapi.Start();

    public void Stop() => BassWasapi.Stop(true);

    public void SetVolume(double volume)
    {
        if (!SupportsVolume)
        {
            // Exclusive mode is pinned to unity gain; see SupportsVolume.
            Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, 1.0);
            return;
        }

        Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, Math.Clamp(volume, 0, 1));
    }

    /// <summary>Human-readable summary for the status bar.</summary>
    public string Describe()
    {
        var rate = SampleRate % 1000 == 0
            ? $"{SampleRate / 1000} kHz"
            : $"{SampleRate / 1000.0:0.#} kHz";

        var depth = DeviceBitDepth > 0 ? $"/{DeviceBitDepth}-bit" : "";

        return Mode == OutputMode.Exclusive
            ? $"Exclusive {rate}{depth} (bit-perfect)"
            : $"Shared {rate}{depth}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        BassWasapi.Stop(true);
        BassWasapi.Free();

        if (_mixer != 0)
        {
            Bass.StreamFree(_mixer);
            _mixer = 0;
        }
    }
}
