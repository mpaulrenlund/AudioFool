using System.Runtime.InteropServices;
using AudioFool.Core.Playback;

namespace AudioFool.Core.Visualizer;

/// <summary>
/// One projectM instance and its playlist, set up calm (session 58, the trial):
/// no hard cuts on beats, a long blend between presets, each preset kept a while,
/// the pack shuffled. Everything here runs on the thread whose OpenGL context it
/// was created in.
/// <para>
/// Speed can't be set: projectM 4.1.6 runs its presets on its own clock, and
/// setting that clock (<c>projectm_set_frame_time</c>) only exists in its
/// unreleased code.
/// </para>
/// </summary>
public sealed class ProjectMSession : IDisposable
{
    /// <summary>Blending from one preset to the next.</summary>
    public const double BlendSeconds = 15;

    /// <summary>How long a preset stays before the blend to the next one starts.</summary>
    public const double PresetSeconds = 90;

    private readonly ProjectMNative.PresetSwitchFailed _failed;
    private readonly ProjectMNative.PresetSwitched _switched;
    private IntPtr _projectM;
    private IntPtr _playlist;

    private ProjectMSession(IntPtr projectM, IntPtr playlist)
    {
        _projectM = projectM;
        _playlist = playlist;

        // Held in fields: the library calls them, and a collected delegate is a crash.
        _failed = OnFailed;
        _switched = OnSwitched;
        ProjectMNative.projectm_playlist_set_preset_switch_failed_event_callback(_playlist, _failed, IntPtr.Zero);
        ProjectMNative.projectm_playlist_set_preset_switched_event_callback(_playlist, _switched, IntPtr.Zero);
    }

    /// <summary>A preset that wouldn't load (path, projectM's message); the playlist moves on.</summary>
    public event Action<string, string>? PresetFailed;

    /// <summary>A new preset has started.</summary>
    public event Action<string>? PresetStarted;

    /// <summary>The version of libprojectM loaded, or null when it can't be loaded.</summary>
    public static string? LibraryVersion()
    {
        try
        {
            ProjectMNative.projectm_get_version_components(out var major, out var minor, out var patch);
            return $"{major}.{minor}.{patch}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts projectM in the OpenGL context current on this thread, with
    /// <paramref name="presets"/> shuffled (none: projectM's idle picture). Null,
    /// with the reason, when the library isn't there or won't start.
    /// </summary>
    public static ProjectMSession? TryCreate(IReadOnlyList<string> presets, IReadOnlyList<string> textureFolders,
        int width, int height, out string? error)
    {
        error = InitialiseGlew();
        if (error is not null)
            return null;

        IntPtr projectM;
        try
        {
            projectM = ProjectMNative.projectm_create();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"libprojectM couldn't be loaded ({ex.GetType().Name}: {ex.Message})";
            return null;
        }

        if (projectM == IntPtr.Zero)
        {
            error = "projectM didn't start (it needs an OpenGL 3.3 context)";
            return null;
        }

        ProjectMNative.projectm_set_window_size(projectM, (nuint)Math.Max(1, width), (nuint)Math.Max(1, height));
        ProjectMNative.projectm_set_texture_search_paths(projectM, textureFolders.ToArray(), (nuint)textureFolders.Count);
        ProjectMNative.projectm_set_hard_cut_enabled(projectM, false);
        ProjectMNative.projectm_set_soft_cut_duration(projectM, BlendSeconds);
        ProjectMNative.projectm_set_preset_duration(projectM, PresetSeconds);
        ProjectMNative.projectm_set_easter_egg(projectM, 0);
        ProjectMNative.projectm_set_aspect_correction(projectM, true);
        ProjectMNative.projectm_set_fps(projectM, 60);

        var playlist = ProjectMNative.projectm_playlist_create(projectM);
        if (playlist == IntPtr.Zero)
        {
            ProjectMNative.projectm_destroy(projectM);
            error = "projectM's playlist didn't start";
            return null;
        }

        var session = new ProjectMSession(projectM, playlist);
        if (presets.Count > 0)
        {
            ProjectMNative.projectm_playlist_add_presets(playlist, presets.ToArray(), (uint)presets.Count, false);
            ProjectMNative.projectm_playlist_set_shuffle(playlist, true);
            ProjectMNative.projectm_playlist_set_retry_count(playlist, 10);
            ProjectMNative.projectm_playlist_play_next(playlist, true);
        }

        return session;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint GlewInit();

    /// <summary>
    /// projectM reaches OpenGL through GLEW and leaves starting it to the host, as
    /// its own frontend does: <c>glewInit</c> in the current context, with
    /// <c>glewExperimental</c> set so a core profile works too. Without it
    /// <c>projectm_create</c> calls through null function pointers (measured: an
    /// access violation). The same glew32.dll projectM loads, from beside the exe.
    /// </summary>
    private static string? InitialiseGlew()
    {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "glew32.dll"), out var glew))
            return "glew32.dll couldn't be loaded";

        if (NativeLibrary.TryGetExport(glew, "glewExperimental", out var experimental))
            Marshal.WriteByte(experimental, 1);

        if (!NativeLibrary.TryGetExport(glew, "glewInit", out var init))
            return "glew32.dll has no glewInit";

        var result = Marshal.GetDelegateForFunctionPointer<GlewInit>(init)();
        return result == 0 ? null : $"GLEW didn't start (error {result})";
    }

    public int PresetCount => (int)ProjectMNative.projectm_playlist_size(_playlist);

    /// <summary>The preset playing (or blending in), as a path; null when there is none.</summary>
    public string? CurrentPreset => PresetAt(ProjectMNative.projectm_playlist_get_position(_playlist));

    public bool Locked
    {
        get => ProjectMNative.projectm_get_preset_locked(_projectM);
        set => ProjectMNative.projectm_set_preset_locked(_projectM, value);
    }

    /// <summary>
    /// How hard it reacts to beats, from the visualizer's intensity 1 to 10: 1 is
    /// projectM's own default at 5, about half at 1 and two and a half times at 10.
    /// </summary>
    public void SetIntensity(int intensity) =>
        ProjectMNative.projectm_set_beat_sensitivity(_projectM, (float)Math.Pow(1.2, Math.Clamp(intensity, 1, 10) - 5));

    public void SetSize(int width, int height) =>
        ProjectMNative.projectm_set_window_size(_projectM, (nuint)Math.Max(1, width), (nuint)Math.Max(1, height));

    /// <summary>
    /// Hands <paramref name="frames"/> frames of <paramref name="interleaved"/> stereo,
    /// from <paramref name="firstFrame"/>, to projectM, which keeps the newest.
    /// </summary>
    public void AddPcm(float[] interleaved, int firstFrame, int frames)
    {
        if (frames > 0)
            ProjectMNative.projectm_pcm_add_float(_projectM, ref interleaved[firstFrame * 2], (uint)frames, ProjectMNative.Stereo);
    }

    /// <summary>Draws a frame into the context's window; the caller swaps buffers.</summary>
    public void Render() => ProjectMNative.projectm_opengl_render_frame(_projectM);

    /// <summary>The next preset, straight away (a key press shouldn't wait out a blend).</summary>
    public void Next() => ProjectMNative.projectm_playlist_play_next(_playlist, true);

    public void Previous() => ProjectMNative.projectm_playlist_play_previous(_playlist, true);

    /// <summary>Drops the preset playing from the playlist and moves on; returns its path.</summary>
    public string? RemoveCurrent()
    {
        var position = ProjectMNative.projectm_playlist_get_position(_playlist);
        var path = PresetAt(position);
        ProjectMNative.projectm_playlist_remove_preset(_playlist, position);
        ProjectMNative.projectm_playlist_play_next(_playlist, true);
        return path;
    }

    /// <summary>
    /// Loads one preset straight away, outside the playlist: for the preset screen,
    /// which goes through the pack one by one (see <see cref="WatchDirectLoads"/>).
    /// </summary>
    public void Load(string presetPath) => ProjectMNative.projectm_load_preset_file(_projectM, presetPath, false);

    /// <summary>
    /// Reports failed loads straight from projectM rather than through the
    /// playlist, which would otherwise answer a failure by playing another preset.
    /// For the preset screen.
    /// </summary>
    public void WatchDirectLoads() =>
        ProjectMNative.projectm_set_preset_switch_failed_event_callback(_projectM, _failed, IntPtr.Zero);

    private string? PresetAt(uint index)
    {
        var text = ProjectMNative.projectm_playlist_item(_playlist, index);
        if (text == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringAnsi(text);
        }
        finally
        {
            ProjectMNative.projectm_playlist_free_string(text);
        }
    }

    private void OnFailed(IntPtr presetFilename, IntPtr message, IntPtr userData)
    {
        var path = Marshal.PtrToStringAnsi(presetFilename) ?? "";
        var why = Marshal.PtrToStringAnsi(message) ?? "";
        PlaybackDiagnostics.Note($"visualizer: preset failed: {path}: {why}");
        PresetFailed?.Invoke(path, why);
    }

    private void OnSwitched(bool isHardCut, uint index, IntPtr userData)
    {
        if (PresetAt(index) is { } path)
            PresetStarted?.Invoke(path);
    }

    public void Dispose()
    {
        if (_playlist != IntPtr.Zero)
        {
            ProjectMNative.projectm_playlist_destroy(_playlist);
            _playlist = IntPtr.Zero;
        }
        if (_projectM != IntPtr.Zero)
        {
            ProjectMNative.projectm_destroy(_projectM);
            _projectM = IntPtr.Zero;
        }
    }
}
