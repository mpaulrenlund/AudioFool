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

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        ThemeService.Apply(settings.Theme);
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
