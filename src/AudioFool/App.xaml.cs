using System.Windows;
using AudioFool.Core.Playback;
using AudioFool.Core.Settings;
using AudioFool.Services;
using AudioFool.ViewModels;

namespace AudioFool;

public partial class App : Application
{
    private BassRuntime? _runtime;
    private AudioEngine? _engine;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A last-resort net so an unexpected fault shows a readable message
        // instead of vanishing the window with a Win32 exit code.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.ToString(),
                "AudioFool hit an unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
        };

        var settings = AppSettings.Load();

        _runtime = new BassRuntime();

        // Constructed on the UI thread on purpose: AudioEngine captures this
        // thread's SynchronizationContext and marshals its events back to it.
        _engine = new AudioEngine(_runtime);

        _viewModel = new MainViewModel(_engine, _runtime, new AlbumArtService(), settings);

        // Before the window is built, not after.
        //
        // A DynamicResource for a Style is resolved while the element is still
        // initialising - the style has to be in hand to build the template - so a
        // theme merged afterwards is too late for it, and the invalidation that
        // would normally fix that up never arrives: Application.Resources only
        // notifies windows the application has open, and this one has not been
        // shown yet. Brush references survive the wrong order because they are
        // evaluated lazily, at first render, which is why getting this backwards
        // looks like a half-applied theme rather than no theme at all.
        //
        // Switching themes from the menu later is unaffected - by then the window
        // is open and does get the invalidation.
        ThemeService.Apply(settings.Theme);

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.WindowBackdropType = ThemeService.Backdrop;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _engine?.Dispose();
        _runtime?.Shutdown();

        base.OnExit(e);
    }
}
