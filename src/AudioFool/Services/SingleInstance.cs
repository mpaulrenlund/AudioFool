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
/// take the foreground), sends its files down the pipe, one path per line, ending
/// in a zero byte, and quits once the first copy answers that it took them. An
/// empty message means "just show yourself": the shortcut clicked again.
/// </para>
/// <para>
/// A copy that is closing stops listening first and lets go of the mutex last
/// (session 61). A later copy started in between gets no answer, keeps trying,
/// and becomes the one copy itself once the mutex is free; before, its files went
/// to the copy that was closing and were lost.
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

    // A later copy: how long it keeps trying to hand over, or to take over from a
    // copy that is closing, before it starts as a copy of its own; and how long
    // each try waits for the pipe.
    private const int HandOverTimeoutMs = 10_000;
    private const int ConnectTimeoutMs = 500;

    // The one copy: how long the UI thread has to take a handover before it is
    // withdrawn, unanswered. The later copy waits a little longer for the answer.
    private const int AcceptTimeoutMs = 3000;
    private const int AnswerTimeoutMs = 4000;
    private const int MessageTimeoutMs = 5000;
    private const byte Taken = 1;

    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _batchTimer;
    private readonly List<string> _batch = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _stopped;

    /// <summary>A later copy handed over (with or without files). On the UI thread.</summary>
    public event Action? Activated;

    /// <summary>Files to open, collected from this copy and later ones. On the UI thread.</summary>
    public event Action<IReadOnlyList<string>>? FilesOpened;

    private SingleInstance(Mutex? mutex, string pipeName)
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
        var pipeName = $"{id}.open";

        var mutex = new Mutex(initiallyOwned: true, $@"Local\{id}.instance", out var createdNew);
        var owned = createdNew;
        if (!owned)
        {
            AllowSetForegroundWindow(AsfwAny);
            var deadline = Environment.TickCount64 + HandOverTimeoutMs;
            while (!owned && Environment.TickCount64 < deadline)
            {
                if (HandOver(pipeName, args))
                {
                    mutex.Dispose();
                    return null;
                }

                // No answer: the one copy is closing (or has closed, or crashed).
                owned = TryTake(mutex);
            }
        }

        if (owned)
        {
            var instance = new SingleInstance(mutex, pipeName);
            instance.Listen();
            instance.Add(args);
            return instance;
        }

        // The copy running never answered and never let go (hung). Better a second
        // copy than nothing happening at all.
        mutex.Dispose();
        return new SingleInstance(null, $"{id}.unused");
    }

    private static bool TryTake(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The one copy ended without letting go: the mutex is this copy's now.
            return true;
        }
    }

    private static bool HandOver(string pipeName, IReadOnlyList<string> args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            pipe.Connect(ConnectTimeoutMs);
            // Full paths: a relative one means this copy's folder, not the other's.
            var message = Encoding.UTF8.GetBytes(string.Join('\n', args.Select(FullPath)) + "\0");
            pipe.Write(message);
            pipe.Flush();

            var answer = new byte[1];
            var read = pipe.ReadAsync(answer, 0, 1);
            return read.Wait(AnswerTimeoutMs) && read.Result == 1 && answer[0] == Taken;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
        {
            // Not listening (closing), or busy with another copy for longer than the
            // connect timeout: a moment's pause, then the caller tries again.
            Thread.Sleep(50);
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

    private void Listen() => _listener = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(token);

                using var messageTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                messageTimeout.CancelAfter(MessageTimeoutMs);
                var message = await ReadMessageAsync(pipe, messageTimeout.Token);
                var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // No answer unless the UI thread took them: the later copy tries
                // again, and takes over once this copy has closed.
                if (Deliver(lines))
                    await pipe.WriteAsync(new[] { Taken }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // A copy that gave up mid-message, or the pipe still held by a copy
                // that is closing. Anything else too: this runs on a thread of its
                // own, where an exception would end the app.
                try
                {
                    await Task.Delay(200, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static async Task<string> ReadMessageAsync(Stream pipe, CancellationToken token)
    {
        using var message = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await pipe.ReadAsync(chunk, token);
            if (count == 0)
                break;

            var end = Array.IndexOf(chunk, (byte)0, 0, count);
            message.Write(chunk, 0, end >= 0 ? end : count);
            if (end >= 0)
                break;
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    /// <summary>
    /// Hands the files to the UI thread and waits for it to take them. False when
    /// this copy is closing, or the UI thread didn't get to them in time; they are
    /// withdrawn then, so they don't arrive twice once the later copy tries again.
    /// </summary>
    private bool Deliver(string[] lines)
    {
        var operation = _dispatcher.InvokeAsync(() =>
        {
            if (_stopped)
                return false;

            Activated?.Invoke();
            Add(lines);
            return true;
        });

        return operation.Wait(TimeSpan.FromMilliseconds(AcceptTimeoutMs)) switch
        {
            DispatcherOperationStatus.Completed => operation.Result,
            DispatcherOperationStatus.Aborted => false,
            // Still waiting: withdrawn. Already running: it will finish.
            _ => !operation.Abort(),
        };
    }

    /// <summary>
    /// No more handovers: called when the window closes, before the app saves and
    /// lets go (<see cref="Dispose"/>). A later copy keeps trying meanwhile.
    /// </summary>
    public void StopListening()
    {
        if (_stopped)
            return;

        _stopped = true;
        _stop.Cancel();

        // So that once the mutex is let go a later copy finds the pipe gone too.
        // Not for long: the listener may be waiting on this thread (Deliver).
        try
        {
            _listener?.Wait(1000);
        }
        catch (AggregateException)
        {
        }
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
        StopListening();
        if (_mutex is null)
            return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread: nothing to release.
        }
        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
