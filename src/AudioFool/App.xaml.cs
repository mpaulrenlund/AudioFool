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

        // A last-resort net so an unexpected fault shows a readable message
        // instead of vanishing the window with a Win32 exit code. With no window
        // left to go back to, the app ends rather than living on unseen.
        DispatcherUnhandledException += (_, args) =>
        {
            ShowError(args.Exception);
            args.Handled = true;
            if (Windows.Count == 0)
                Shutdown(1);
        };

        try
        {
            Launch(e.Args);
        }
        catch (Exception ex)
        {
            // Before the window is up. Carrying on would leave a copy with no
            // window holding the one-copy lock, and every later start would hand
            // its songs to it and show nothing (session 61).
            ShowError(ex);
            Shutdown(1);
        }
    }

    private void Launch(string[] args)
    {
        // One copy: a song opened from Explorer goes to the copy already running.
        _instance = SingleInstance.Start(args);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        // The X that sometimes ignores clicks: a log that changes nothing, and a
        // safety net that clicks the X when WPF-UI loses a click on it.
        TitleBarClickLog.Install();
        CloseButtonSafetyNet.Install();

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
        // From here on the app only saves and exits. (Closing can still be
        // put off while a tag save finishes; a song opened then is lost.)
        window.Closed += (_, _) => _instance.StopListening();

        window.Show();
    }

    private static void ShowError(Exception ex) => MessageBox.Show(
        ex.ToString(),
        "AudioFool hit an unexpected error",
        MessageBoxButton.OK,
        MessageBoxImage.Error);

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _engine?.Dispose();
        _runtime?.Shutdown();

        // Last, once everything is saved: a copy started meanwhile has been
        // waiting, and takes over now.
        _instance?.Dispose();

        base.OnExit(e);
    }
}
