using AudioFool.Core.Models;

namespace AudioFool.Core.Playback;

/// <summary>
/// What is coming out of the speakers: the track (null for silence, nothing
/// playing) and where in it. See <see cref="AudioEngine.Heard"/>.
/// </summary>
public readonly record struct HeardPoint(Track? Track, TimeSpan Position);
