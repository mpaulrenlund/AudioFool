using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using AudioFool.Core.Playback;
using AudioFool.Core.Updates;

namespace AudioFool.ViewModels;

/// <summary>
/// The Refresh button on the logo menu's About row (2026-11-20, the user's request):
/// asks GitHub for the newest release; when it's newer, offers to download and
/// install it, then restarts into it. Otherwise says there's nothing new.
/// The swap itself is <see cref="UpdateInstaller"/>.
/// </summary>
public sealed partial class MainViewModel
{
    private const string UpdateTitle = "Check for Updates";
    private const string AfterUpdateArg = "--after-update";

    private static Version CurrentVersion =>
        typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0);

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (Application.Current.MainWindow is not { } owner)
            return;

        var check = new UpdateCheck();
        var before = StatusText;
        StatusText = "Checking for updates…";

        ReleaseInfo? latest;
        try
        {
            latest = await check.GetLatestAsync();
        }
        catch (UpdateCheckException ex)
        {
            StatusText = before;
            PromptWindow.Inform(owner, UpdateTitle, $"Couldn't check for updates. {ex.Message}");
            return;
        }

        StatusText = before;
        var current = CurrentVersion;
        if (latest is null || !UpdateCheck.IsNewer(latest.Version, current))
        {
            PromptWindow.Inform(owner, UpdateTitle, NoUpdateMessage());
            return;
        }

        var shown = "v" + latest.Version.ToString(3);
        if (!PromptWindow.Confirm(owner, UpdateTitle,
                UpdateAvailableMessage(shown, _engine.State == PlaybackState.Playing), "Install"))
            return;

        await InstallUpdateAsync(owner, check, latest, shown);
    }

    // The dialogs' words, apart so ThemeLab renders the same ones.
    private static string NoUpdateMessage() =>
        $"No new version is available. You have the latest, {AppVersion.Text}.";

    private static string UpdateAvailableMessage(string shown, bool playing) =>
        $"AudioFool {shown} is available. You have {AppVersion.Text}.\n\n"
        + "Download and install it? AudioFool will close and start again."
        + (playing ? " The music will stop." : "");

    private async Task InstallUpdateAsync(Window owner, UpdateCheck check, ReleaseInfo release, string shown)
    {
        var appFolder = AppContext.BaseDirectory;
        var work = Path.Combine(Path.GetTempPath(), "AudioFool-update");
        var zip = Path.Combine(work, release.ZipName);
        var staging = Path.Combine(work, "files");

        try
        {
            Directory.CreateDirectory(work);

            var progress = new Progress<double>(share =>
                StatusText = $"Downloading AudioFool {shown}… {share:P0}");
            StatusText = $"Downloading AudioFool {shown}…";
            await check.DownloadAsync(release, zip, progress);

            StatusText = $"Installing AudioFool {shown}…";
            await Task.Run(() =>
            {
                UpdateInstaller.Extract(zip, staging);
                UpdateInstaller.Swap(staging, appFolder);
            });
        }
        catch (Exception ex) when (ex is UpdateCheckException or IOException or UnauthorizedAccessException)
        {
            ReportProblem($"The update to {shown} didn't install.");
            PromptWindow.Inform(owner, UpdateTitle,
                $"Couldn't install AudioFool {shown}. {ex.Message} Nothing was changed.");
            return;
        }
        finally
        {
            TryDeleteFolder(work);
        }

        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(appFolder, UpdateInstaller.ExeName))
            {
                UseShellExecute = false,
                WorkingDirectory = appFolder,
                ArgumentList = { AfterUpdateArg, Environment.ProcessId.ToString() },
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // The files are in place, so the next start is the new version anyway.
            StatusText = $"AudioFool {shown} is installed and starts next time.";
            PromptWindow.Inform(owner, UpdateTitle,
                $"AudioFool {shown} is installed, but couldn't be started: {ex.Message} "
                + "Close AudioFool and open it again to use it.");
            return;
        }

        _engine.Stop();
        StatusText = $"Restarting into AudioFool {shown}…";

        // The ordinary close: settings and the library are saved, a tag save still
        // running is finished first. The new copy waits for this one to end.
        owner.Close();
    }

    /// <summary>
    /// At start-up: when started by an update, waits for the copy that installed it
    /// to close. Either way, removes what an earlier update left behind. Returns the
    /// arguments without the update's own.
    /// </summary>
    public static string[] TakeAfterUpdateArgs(string[] args)
    {
        var at = Array.IndexOf(args, AfterUpdateArg);
        if (at >= 0)
        {
            if (at + 1 < args.Length && int.TryParse(args[at + 1], out var pid))
                WaitForExit(pid);

            args = args.Where((_, i) => i != at && i != at + 1).ToArray();
        }

        try
        {
            UpdateInstaller.CleanUp(AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }

        return args;
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(TimeSpan.FromSeconds(60));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
