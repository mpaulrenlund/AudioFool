using AudioFool.Core.Playback;

namespace AudioFool.Core.Analysis;

/// <summary>
/// The visualizer's side of the chain, one call per video frame: read the window
/// centred on what is being heard from the tap (<see cref="AudioEngine.TryReadHeard"/>),
/// convert it (<see cref="TapConverter"/>) and analyse it (<see cref="LiveSpectrum"/>).
/// <para>
/// When there is nothing to hear (paused, stopped, the tap just turned on, the
/// device reopening) it feeds silence instead, so the picture sinks to rest
/// rather than freezing. A new layout (another rate, DoP or not) gets a new
/// converter, and a new analysis only if the converted rate changes; the
/// intensity carries over. Nothing is allocated per frame otherwise.
/// </para>
/// </summary>
public sealed class VisualizerFeed
{
    private readonly Func<TapFormat?> _format;
    private readonly Func<float[], TapFormat, int, bool> _read;
    private readonly int _fftSize;
    private readonly int _bands;
    private TapConverter? _converter;
    private LiveSpectrum? _spectrum;
    private float[] _silence = [];
    private int _intensity = LiveSpectrum.DefaultIntensity;

    /// <param name="format">The tap's layout, or null while it is off.</param>
    /// <param name="read">Reads a window: destination, layout, frames ahead of what is heard.</param>
    public VisualizerFeed(Func<TapFormat?> format, Func<float[], TapFormat, int, bool> read, int fftSize = 2048, int bands = 32)
    {
        _format = format;
        _read = read;
        _fftSize = fftSize;
        _bands = bands;
    }

    /// <summary>The feed for the engine's tap. Turning the tap on and off is the caller's.</summary>
    public static VisualizerFeed For(AudioEngine engine, int bands = 32) =>
        new(() => engine.TapFormat, engine.TryReadHeard, bands: bands);

    /// <summary>The analysis to draw from; null until the tap has said what it holds.</summary>
    public LiveSpectrum? Spectrum => _spectrum;

    /// <summary>Whether the last frame had sound to read, rather than silence fed in.</summary>
    public bool Heard { get; private set; }

    /// <summary>1 (calm) to 10 (lively), passed to the analysis; see <see cref="LiveSpectrum.Intensity"/>.</summary>
    public int Intensity
    {
        get => _intensity;
        set
        {
            _intensity = Math.Clamp(value, LiveSpectrum.LowestIntensity, LiveSpectrum.HighestIntensity);
            if (_spectrum is not null)
                _spectrum.Intensity = _intensity;
        }
    }

    /// <summary>
    /// One frame, <paramref name="seconds"/> after the last. <paramref name="listening"/>
    /// false (paused or stopped) feeds silence whatever the tap holds.
    /// </summary>
    public void Next(double seconds, bool listening)
    {
        if (_format() is { } format && (_converter is null || _converter.Format != format))
            Prepare(format);

        if (_converter is null || _spectrum is null)
            return;

        var channels = _converter.Format.Channels;

        // Centred on now: half a window past what is heard, in the tap's frames.
        var ahead = _fftSize / 2 * (_converter.Format.SampleRate / _converter.OutputRate);
        Heard = listening && _read(_converter.Input, _converter.Format, ahead);
        if (Heard)
        {
            _converter.Convert();
            _spectrum.Update(_converter.Output, channels, seconds);
        }
        else
        {
            _spectrum.Update(_silence, channels, seconds);
        }
    }

    private void Prepare(TapFormat format)
    {
        _converter = new TapConverter(format, _fftSize);
        if (_spectrum is null || _spectrum.SampleRate != _converter.OutputRate)
            _spectrum = new LiveSpectrum(_converter.OutputRate, _fftSize, _bands) { Intensity = _intensity };
        if (_silence.Length != _fftSize * format.Channels)
            _silence = new float[_fftSize * format.Channels];
    }
}
