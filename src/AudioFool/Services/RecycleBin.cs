using System.IO;
using System.Runtime.InteropServices;

namespace AudioFool.Services;

/// <summary>
/// Sends a file to the Recycle Bin, silently: no confirmation, no progress, no
/// error dialog. Only on a fixed drive (the library's NTFS SSD has a bin); a
/// removable drive such as an SD card has none, and the shell would delete the
/// file outright there, so false is returned and the file is left alone.
/// </summary>
public static class RecycleBin
{
    public static bool Send(string path)
    {
        try
        {
            if (Path.GetPathRoot(Path.GetFullPath(path)) is not { Length: > 0 } root
                || new DriveInfo(root).DriveType != DriveType.Fixed)
                return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var operation = new ShFileOperation
        {
            Func = FoDelete,
            // Double-null-terminated list of one.
            From = path + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
        };

        return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted && !File.Exists(path);
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation operation);
}
