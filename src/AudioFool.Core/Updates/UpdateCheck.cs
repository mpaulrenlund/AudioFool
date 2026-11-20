using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioFool.Core.Updates;

/// <summary>A release on GitHub that the app can install: its version and its zip.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string ZipName, Uri ZipUrl, long ZipSize);

/// <summary>
/// Asks GitHub for AudioFool's newest release (the logo menu's Refresh button on the
/// About row, 2026-11-20, the user's request). The repository is public, so no
/// sign-in: GitHub allows 60 unsigned requests an hour from one address, plenty for
/// a button. The zip is the one the release skill uploads,
/// <c>AudioFool-&lt;version&gt;-win-x64.zip</c>; a release without it is not offered.
/// </summary>
public sealed class UpdateCheck
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/mpaulrenlund/AudioFool/releases/latest";

    private const string UserAgent = "AudioFool/1.0 (personal music player)";

    private static readonly HttpClient SharedHttp = CreateClient();

    private readonly HttpClient _http;

    public UpdateCheck(HttpClient? http = null) => _http = http ?? SharedHttp;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // The check itself; a download sets its own, longer, limit.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// The newest release, or null when the repository has none with a zip.
    /// Throws <see cref="UpdateCheckException"/> with a short, readable reason when
    /// GitHub can't be reached or answers oddly.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateCheckException("GitHub didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException("GitHub couldn't be reached. Are you online?", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateCheckException("GitHub asked to wait a while before checking again.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateCheckException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseLatest(json);
        }
    }

    /// <summary>
    /// Downloads the release's zip to <paramref name="path"/>, reporting the share
    /// done (0 to 1) when the size is known. The file is checked against the size
    /// GitHub gave; a short download throws and leaves no file behind.
    /// </summary>
    public async Task DownloadAsync(ReleaseInfo release, string path, IProgress<double>? progress = null,
                                    CancellationToken cancel = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));

            using var response = await _http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                                            .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateCheckException($"The download failed: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            var total = response.Content.Headers.ContentLength ?? release.ZipSize;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }

            if (release.ZipSize > 0 && new FileInfo(path).Length != release.ZipSize)
                throw new UpdateCheckException("The download was incomplete.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                                       && !cancel.IsCancellationRequested)
        {
            TryDelete(path);
            throw new UpdateCheckException("The download was interrupted. Are you online?", ex);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// The release in GitHub's answer for <c>releases/latest</c>, or null when it has
    /// no version in its tag or no zip of the expected name.
    /// </summary>
    public static ReleaseInfo? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.GetString() is not { } tag
                || ParseVersion(tag) is not { } version)
                return null;

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                return null;

            var zipName = ZipNameFor(version);
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), zipName, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var urlElement)
                    && Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url)
                    && url.Scheme == Uri.UriSchemeHttps)
                {
                    var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
                    return new ReleaseInfo(version, tag, zipName, url, size);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            throw new UpdateCheckException("GitHub's answer couldn't be read.");
        }
    }

    /// <summary>"v0.12.0" or "0.12.0" as 0.12.0; null when it isn't a version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        return Version.TryParse(trimmed, out var version) ? Normalise(version) : null;
    }

    /// <summary>True when <paramref name="latest"/> is newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version latest, Version current) => Normalise(latest) > Normalise(current);

    /// <summary>The release zip's name, as the release skill uploads it.</summary>
    public static string ZipNameFor(Version version) =>
        $"AudioFool-{Normalise(version).ToString(3)}-win-x64.zip";

    // 0.12 and 0.12.0 and 0.12.0.0 are one version: the build and revision count as 0.
    private static Version Normalise(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A check or download that failed, with a reason fit to show the user.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message, Exception? inner = null) : base(message, inner) { }
}
