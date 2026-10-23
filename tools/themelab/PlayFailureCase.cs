using System.IO;
using AudioFool;
using AudioFool.Core.Models;
using AudioFool.Core.Playback;
using AudioFool.ViewModels;

namespace ThemeLab;

/// <summary>
/// --window playfailure (session 71): the real engine and BASS told a missing
/// file, an unplugged drive, a file another program holds and a file that isn't
/// audio apart, and the status line each gets. Every file fails to open, so
/// nothing reaches the sound device. Scratch files only, deleted after.
/// </summary>
internal static partial class Program
{
    private static int RunPlayFailure(MainViewModel vm, AudioEngine engine)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ThemeLabPlayFailure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var describe = typeof(MainViewModel).GetMethod("DescribePlayFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        void Try(string path, PlayFailureKind expected)
        {
            var track = new Track { FilePath = path, Title = Path.GetFileName(path) };
            var played = engine.Play([track], 0);
            var line = (string)describe.Invoke(vm, [track])!;
            Check(!played && engine.LastPlayFailure?.Kind == expected,
                $"{expected}: {engine.LastPlayFailure?.Kind} '{line}'");
        }

        try
        {
            Try(Path.Combine(dir, "gone.flac"), PlayFailureKind.FileMissing);

            var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).First(c => !used.Contains(c));
            Try($@"{free}:\Music\Artist\Album\01 Song.flac", PlayFailureKind.DriveMissing);

            var notAudio = Path.Combine(dir, "not audio.flac");
            File.WriteAllText(notAudio, string.Concat(Enumerable.Repeat("This is not a FLAC file. ", 400)));
            Try(notAudio, PlayFailureKind.Format);

            var held = Path.Combine(dir, "held.flac");
            File.WriteAllText(held, "x");
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Try(held, PlayFailureKind.FileUnopenable);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }
}
