using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;

namespace AudioFool.Services;

/// <summary>
/// One AudioFool per user and sign-in. Opening a song from Explorer used to start
/// a second, separate copy that ignored the song (the user's report, session 59).
/// <para>
/// The first copy owns a named mutex and listens on a named pipe. A later copy
/// finds the mutex taken, lets the first copy come to the front
/// (AllowSetForegroundWindow: Windows only lets the process the user just started
/// take the foreground), sends its files down the pipe, one path per line, and
/// quits. An empty message means "just show yourself": the shortcut clicked again.
/// </para>
/// <para>
/// Explorer starts one copy per file when several are opened at once, so the
/// files are collected until none has arrived for <see cref="BatchQuietMs"/>, and
/// handed on as one batch: <see cref="FilesOpened"/>. The first copy's own files
/// join the same batch. <see cref="Activated"/> is raised for every handover at
/// once, before the batch.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int BatchQuietMs = 500;
    private const int ConnectTimeoutMs = 5000;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _batchTimer;
    private readonly List<string> _batch = [];

    /// <summary>A later copy handed over (with or without files). On the UI thread.</summary>
    public event Action? Activated;

    /// <summary>Files to open, collected from this copy and later ones. On the UI thread.</summary>
    public event Action<IReadOnlyList<string>>? FilesOpened;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _batchTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(BatchQuietMs),
        };
        _batchTimer.Tick += (_, _) => FlushBatch();
    }

    /// <summary>
    /// Becomes the one copy and starts listening, or hands <paramref name="args"/>
    /// to the copy already running. Null means they were handed over and this copy
    /// should quit. <paramref name="name"/> keeps a test apart from the real app.
    /// </summary>
    public static SingleInstance? Start(IReadOnlyList<string> args, string name = "AudioFool")
    {
        // Per user and per sign-in session: another user, or the same user over
        // remote desktop, gets a copy of their own.
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var id = $"{name}-{user}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

        var mutex = new Mutex(initiallyOwned: true, $@"Local\{id}.instance", out var createdNew);
        if (createdNew)
        {
            var instance = new SingleInstance(mutex, $"{id}.open");
            instance.Listen();
            instance.Add(args);
            return instance;
        }

        mutex.Dispose();
        if (HandOver($"{id}.open", args))
            return null;

        // The copy running didn't answer (hung, or closing). Better a second copy
        // than nothing happening at all.
        return new SingleInstance(new Mutex(), $"{id}.unused");
    }

    private static bool HandOver(string pipeName, IReadOnlyList<string> args)
    {
        try
        {
            AllowSetForegroundWindow(AsfwAny);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);
            // Full paths: a relative one means this copy's folder, not the other's.
            var message = Encoding.UTF8.GetBytes(string.Join('\n', args.Select(FullPath)));
            pipe.Write(message);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FullPath(string arg)
    {
        try
        {
            return Path.GetFullPath(arg.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return arg;
        }
    }

    private void Listen()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    _dispatcher.BeginInvoke(() =>
                    {
                        Activated?.Invoke();
                        Add(lines);
                    });
                }
                catch (IOException)
                {
                    // A copy that gave up mid-message: wait for the next.
                }
            }
        })
        {
            IsBackground = true,
            Name = "Single instance",
        };
        thread.Start();
    }

    private void Add(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return;

        _batch.AddRange(files);
        _batchTimer.Stop();
        _batchTimer.Start();
    }

    private void FlushBatch()
    {
        _batchTimer.Stop();
        if (_batch.Count == 0)
            return;

        var files = _batch.ToList();
        _batch.Clear();
        FilesOpened?.Invoke(files);
    }

    public void Dispose()
    {
        _batchTimer.Stop();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (the fallback above): nothing to release.
        }
        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
