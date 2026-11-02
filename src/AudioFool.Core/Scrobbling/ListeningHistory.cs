using System.Globalization;
using System.Text.Json;

namespace AudioFool.Core.Scrobbling;

/// <summary>The spans Last.fm keeps top lists for.</summary>
public enum ListeningPeriod { Week, Month, ThreeMonths, SixMonths, Year, AllTime }

public static class ListeningPeriods
{
    public static IReadOnlyList<ListeningPeriod> All { get; } = Enum.GetValues<ListeningPeriod>();

    /// <summary>The <c>period</c> parameter Last.fm takes.</summary>
    public static string ApiName(ListeningPeriod period) => period switch
    {
        ListeningPeriod.Week => "7day",
        ListeningPeriod.Month => "1month",
        ListeningPeriod.ThreeMonths => "3month",
        ListeningPeriod.SixMonths => "6month",
        ListeningPeriod.Year => "12month",
        _ => "overall",
    };

    public static string Label(ListeningPeriod period) => period switch
    {
        ListeningPeriod.Week => "7 Days",
        ListeningPeriod.Month => "1 Month",
        ListeningPeriod.ThreeMonths => "3 Months",
        ListeningPeriod.SixMonths => "6 Months",
        ListeningPeriod.Year => "12 Months",
        _ => "All Time",
    };
}

/// <summary>
/// The user's totals from user.getInfo. The three counts are newer than the
/// rest of the API and null when an answer leaves them out.
/// </summary>
public sealed record ListenerProfile(
    string UserName, long Scrobbles, DateTimeOffset? Registered, long? Artists, long? Albums, long? Tracks);

/// <summary>One line of a top list. <see cref="Artist"/> is null for the artists list itself.</summary>
public sealed record TopEntry(string Name, string? Artist, long Plays);

/// <summary>One recent play. A song still playing has no time.</summary>
public sealed record RecentPlay(string Track, string Artist, string Album, DateTimeOffset? PlayedAt, bool NowPlaying);

/// <summary>
/// Reads Last.fm's JSON, which is XML turned into JSON and carries its quirks:
/// numbers come as strings, text nodes as <c>{"#text": ...}</c>, attributes
/// under <c>@attr</c>, and a list of one item as the item itself.
/// </summary>
public static class ListeningJson
{
    public static ListenerProfile Profile(JsonElement root)
    {
        if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            throw new LastFmException(0, "Last.fm returned no profile.");

        DateTimeOffset? registered = null;
        if (user.TryGetProperty("registered", out var reg)
            && (Number(reg, "unixtime") ?? Number(reg, "#text")) is { } seconds and > 0)
            registered = DateTimeOffset.FromUnixTimeSeconds(seconds);

        return new ListenerProfile(
            Text(user, "name"),
            Number(user, "playcount") ?? 0,
            registered,
            Number(user, "artist_count"),
            Number(user, "album_count"),
            Number(user, "track_count"));
    }

    /// <param name="listName">"topartists", "topalbums" or "toptracks".</param>
    /// <param name="itemName">"artist", "album" or "track".</param>
    public static IReadOnlyList<TopEntry> Top(JsonElement root, string listName, string itemName, int limit)
    {
        if (!root.TryGetProperty(listName, out var list))
            return [];

        var isArtists = itemName == "artist";
        return Items(list, itemName)
            .Select(e => new TopEntry(Text(e, "name"), isArtists ? null : Text(e, "artist"), Number(e, "playcount") ?? 0))
            .Where(e => e.Name.Length > 0)
            .Take(limit)
            .ToList();
    }

    /// <summary>At most <paramref name="limit"/> plays: Last.fm sends one more when a song is playing.</summary>
    public static IReadOnlyList<RecentPlay> Recent(JsonElement root, int limit)
    {
        if (!root.TryGetProperty("recenttracks", out var list))
            return [];

        return Items(list, "track")
            .Select(e =>
            {
                var nowPlaying = e.TryGetProperty("@attr", out var attr) && Text(attr, "nowplaying") == "true";
                DateTimeOffset? at = !nowPlaying && e.TryGetProperty("date", out var date) && Number(date, "uts") is { } uts
                    ? DateTimeOffset.FromUnixTimeSeconds(uts)
                    : null;
                return new RecentPlay(Text(e, "name"), Text(e, "artist"), Text(e, "album"), at, nowPlaying);
            })
            .Where(p => p.Track.Length > 0)
            .Take(limit)
            .ToList();
    }

    private static IEnumerable<JsonElement> Items(JsonElement list, string name)
    {
        if (list.ValueKind != JsonValueKind.Object || !list.TryGetProperty(name, out var items))
            return [];

        return items.ValueKind switch
        {
            JsonValueKind.Array => items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList(),
            JsonValueKind.Object => [items],
            _ => [],
        };
    }

    /// <summary>A string, a number, or an object's <c>#text</c> or <c>name</c>; "" when none.</summary>
    private static string Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Object when value.TryGetProperty("#text", out var text) => text.ValueKind == JsonValueKind.String ? text.GetString()?.Trim() ?? "" : text.GetRawText(),
            JsonValueKind.Object => Text(value, "name"),
            _ => "",
        };
    }

    private static long? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }
}

/// <summary>How Listening History words a time: "12 min ago", "Yesterday", "Oct 30".</summary>
public static class ListeningTime
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static string Ago(RecentPlay play, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (play.NowPlaying || play.PlayedAt is not { } at)
            return "Now playing";

        var gap = now - at;
        if (gap < TimeSpan.FromMinutes(1))
            return "Just now";
        if (gap < TimeSpan.FromHours(1))
            return $"{(int)gap.TotalMinutes} min ago";

        var day = TimeZoneInfo.ConvertTime(at, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (day == today)
            return (int)gap.TotalHours == 1 ? "1 hour ago" : $"{(int)gap.TotalHours} hours ago";
        if (day == today.AddDays(-1))
            return "Yesterday";

        return day.Year == today.Year
            ? day.ToString("MMM d", English)
            : day.ToString("MMM d, yyyy", English);
    }

    /// <summary>"Mar 2014": the month someone joined.</summary>
    public static string Month(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).ToString("MMM yyyy", English);
}
