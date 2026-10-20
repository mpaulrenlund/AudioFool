using System.Runtime.InteropServices;

namespace AudioFool.Core.Visualizer;

/// <summary>
/// libprojectM 4.1.6's C API (and its playlist library), from the headers in the
/// MSYS2 UCRT64 package the DLLs came from (session 58). Every call needs the
/// OpenGL context projectM was created in to be current on the calling thread.
/// Strings are the system code page, as the library's file functions take them.
/// </summary>
internal static class ProjectMNative
{
    private const string Core = "libprojectM-4-4.dll";
    private const string Playlist = "libprojectM-4-playlist-4.dll";

    public const int Stereo = 2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PresetSwitchFailed(IntPtr presetFilename, IntPtr message, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PresetSwitched([MarshalAs(UnmanagedType.U1)] bool isHardCut, uint index, IntPtr userData);

    // ------------------------------------------------------------------ core.h

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr projectm_create();

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_destroy(IntPtr instance);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern void projectm_load_preset_file(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string filename,
        [MarshalAs(UnmanagedType.U1)] bool smoothTransition);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_get_version_components(out int major, out int minor, out int patch);

    // ------------------------------------------------------------------ render_opengl.h, audio.h

    /// <summary>Draws a frame into framebuffer 0: the window the context belongs to.</summary>
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_opengl_render_frame(IntPtr instance);

    /// <summary><paramref name="count"/> is frames (samples per channel); stereo is interleaved L R.</summary>
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_pcm_add_float(IntPtr instance, ref float samples, uint count, int channels);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_pcm_get_max_samples();

    // ------------------------------------------------------------------ callbacks.h

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_preset_switch_failed_event_callback(IntPtr instance, PresetSwitchFailed? callback, IntPtr userData);

    // ------------------------------------------------------------------ parameters.h

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern void projectm_set_texture_search_paths(IntPtr instance,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] paths, nuint count);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_hard_cut_enabled(IntPtr instance, [MarshalAs(UnmanagedType.U1)] bool enabled);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_soft_cut_duration(IntPtr instance, double seconds);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_preset_duration(IntPtr instance, double seconds);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_easter_egg(IntPtr instance, float value);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_fps(IntPtr instance, int fps);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_aspect_correction(IntPtr instance, [MarshalAs(UnmanagedType.U1)] bool enabled);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_preset_locked(IntPtr instance, [MarshalAs(UnmanagedType.U1)] bool locked);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool projectm_get_preset_locked(IntPtr instance);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_window_size(IntPtr instance, nuint width, nuint height);

    // ------------------------------------------------------------------ the playlist library

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr projectm_playlist_create(IntPtr projectm);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_destroy(IntPtr playlist);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern uint projectm_playlist_add_presets(IntPtr playlist,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] filenames, uint count,
        [MarshalAs(UnmanagedType.U1)] bool allowDuplicates);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool projectm_playlist_remove_preset(IntPtr playlist, uint index);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_size(IntPtr playlist);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr projectm_playlist_item(IntPtr playlist, uint index);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_free_string(IntPtr text);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_shuffle(IntPtr playlist, [MarshalAs(UnmanagedType.U1)] bool shuffle);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_retry_count(IntPtr playlist, uint retryCount);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_get_position(IntPtr playlist);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_play_next(IntPtr playlist, [MarshalAs(UnmanagedType.U1)] bool hardCut);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_play_previous(IntPtr playlist, [MarshalAs(UnmanagedType.U1)] bool hardCut);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_preset_switched_event_callback(IntPtr playlist, PresetSwitched? callback, IntPtr userData);

    [DllImport(Playlist, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_preset_switch_failed_event_callback(IntPtr playlist, PresetSwitchFailed? callback, IntPtr userData);
}
