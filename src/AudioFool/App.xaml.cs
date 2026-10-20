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
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One copy: a song opened from Explorer goes to the copy already running.
        _instance = SingleInstance.Start(e.Args);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

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

        // Diagnostics for the X that sometimes ignores clicks; changes nothing.
        TitleBarClickLog.Install();

        var settings = AppSettings.Load();

        _runtime = new BassRuntime();

        // Constructed on the UI thread on purpose: AudioEngine captures this
        // thread's SynchronizationContext and marshals its events back to it.
        _engine = new AudioEngine(_runtime);

        _viewModel = new MainViewModel(_engine, _runtime, new AlbumArtService(new Core.Art.ArtDiskCache(Core.Art.ArtDiskCache.DefaultFolder)), settings);

        // The theme saved at the last switch (Dark when none was), before the
        // window is built, not after.
        //
        // A DynamicResource for a Style is resolved while the element is still
        // initialising - the style has to be in hand to build the template - so a
        // theme merged afterwards is too late for it, and the invalidation that
        // would normally fix that up never arrives: Application.Resources only
        // notifies windows the application has open, and this one has not been
        // shown yet. Brush references survive the wrong order because they are
        // evaluated lazily, at first render, which is why getting this backwards
        // looks like a half-applied theme rather than no theme at all. A switch
        // later on is different: every window it has to reach is open by then.
        ThemeService.Apply(settings.Theme);

        var window = new MainWindow(_viewModel);
        MainWindow = window;

        _instance.Activated += window.BringToFront;
        _instance.FilesOpened += _viewModel.OpenFiles;

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _viewModel?.Dispose();
        _engine?.Dispose();
        _runtime?.Shutdown();

        base.OnExit(e);
    }
}
