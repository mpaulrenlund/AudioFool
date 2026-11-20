namespace AudioFool;

/// <summary>
/// The app's version as shown to the user, "v0.11.0": the csproj's Version, read from
/// the assembly as the intro screen does. The logo menu's About line shows it.
/// </summary>
public static class AppVersion
{
    public static string Text { get; } =
        "v" + (typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "?");
}
