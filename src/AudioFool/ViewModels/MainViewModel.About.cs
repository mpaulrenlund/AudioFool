using System.Windows;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Playback;

namespace AudioFool.ViewModels;

/// <summary>
/// About, at the bottom of the logo menu (session 64, the user's request): the
/// PlayStation's start-up over the main window. Music playing pauses for it and
/// carries on afterwards, the user's call.
/// </summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    private void ShowAbout()
    {
        if (Application.Current.MainWindow is not { } owner)
            return;

        var wasPlaying = _engine.State == PlaybackState.Playing;
        if (wasPlaying)
            _engine.Pause();

        // Bit-Perfect holds the device even paused, so the intro couldn't be heard:
        // it is lent for the intro and taken back, the track where it was.
        using (_engine.IsBitPerfect ? _engine.LendDevice() : null)
            new IntroWindow(owner, _volumeState.Effective).ShowDialog();

        if (wasPlaying)
            _engine.Resume();
    }
}
