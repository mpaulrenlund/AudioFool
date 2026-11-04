using System.Text.Json;

namespace AudioFool.Core.Genius;

/// <summary>
/// Reads the answers of genius.com's own web API (the one its pages use), each
/// asked for with <c>text_format=plain</c>, so every text arrives as
/// <c>{"plain": "..."}</c>. Each method takes the <c>response</c> object.
/// Anything missing is skipped rather than failing the page: Genius leaves
/// fields out freely.
/// </summary>
public static class GeniusJson
{
    /// <summary>The songs of a <c>search/song</c> answer, in Genius's order.</summary>
    public static IReadOnlyList<GeniusHit> SearchHits(JsonElement response)
    {
        List<GeniusHit> hits = [];
        if (!response.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            return hits;

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("hits", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var hit in list.EnumerateArray())
            {
                if (hit.TryGetProperty("result", out var result) && Hit(result) is { } song
                    && (Text(hit, "type") ?? Text(result, "_type")) == "song")
                    hits.Add(song);
            }
        }
        return hits;
    }

    /// <summary>The <c>song</c> of a <c>songs/{id}</c> answer.</summary>
    public static GeniusSong Song(JsonElement response)
    {
        var song = response.GetProperty("song");
        var hit = Hit(song) ?? throw new GeniusException("Genius sent a song without its name.");

        var about = Plain(song, "description");
        if (about is "?" or "")
            about = null;

        var album = song.TryGetProperty("album", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "name") : null;
        var released = Text(song, "release_date_for_display");

        List<GeniusCredit> credits = [];
        AddCredit(credits, "Written by", Names(song, "writer_artists"));
        AddCredit(credits, "Produced by", Names(song, "producer_artists"));
        AddCredit(credits, "Released", released is null ? [] : [released]);
        AddCredit(credits, "Recorded at", Text(song, "recording_location") is { } where ? [where] : []);
        if (song.TryGetProperty("custom_performances", out var performances) && performances.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in performances.EnumerateArray())
            {
                if (Text(p, "label") is { } label)
                    AddCredit(credits, CreditLabel(label), Names(p, "artists"));
            }
        }

        List<GeniusConnection> connections = [];
        if (song.TryGetProperty("song_relationships", out var relationships) && relationships.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in relationships.EnumerateArray())
            {
                var type = Text(r, "relationship_type") ?? Text(r, "type");
                if (type is null or "translation_of" or "translations")
                    continue;

                List<GeniusHit> songs = [];
                if (r.TryGetProperty("songs", out var list) && list.ValueKind == JsonValueKind.Array)
                    songs.AddRange(list.EnumerateArray().Select(Hit).OfType<GeniusHit>());
                if (songs.Count > 0)
                    connections.Add(new GeniusConnection(ConnectionLabel(type), songs));
            }
        }

        return new GeniusSong(hit.Id, hit.Title, hit.Artist, hit.Url, about, album, released, credits, connections);
    }

    /// <summary>
    /// The explanations of a <c>referents?song_id=</c> answer, in the order of
    /// the lyrics. The write-up's own entry is left out (it is the About text),
    /// and so are explanations Genius hasn't accepted yet.
    /// </summary>
    public static IReadOnlyList<GeniusAnnotation> Annotations(JsonElement response)
    {
        List<GeniusAnnotation> notes = [];
        if (!response.TryGetProperty("referents", out var referents) || referents.ValueKind != JsonValueKind.Array)
            return notes;

        foreach (var r in referents.EnumerateArray())
        {
            if (Bool(r, "is_description") || Text(r, "classification") == "unreviewed")
                continue;
            if (Text(r, "fragment")?.Trim() is not { Length: > 0 } fragment)
                continue;
            if (!r.TryGetProperty("annotations", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;

            var first = list.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object || Plain(first, "body") is not { Length: > 0 } body)
                continue;

            notes.Add(new GeniusAnnotation(fragment, body, Bool(first, "verified")));
        }
        return notes;
    }

    /// <summary>The answered questions of a <c>questions?song_id=</c> answer, pinned ones first as Genius sends them.</summary>
    public static IReadOnlyList<GeniusAnswer> Answers(JsonElement response)
    {
        List<GeniusAnswer> answers = [];
        if (!response.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return answers;

        foreach (var q in questions.EnumerateArray())
        {
            if (Plain(q, "body") is not { Length: > 0 } question)
                continue;
            if (!q.TryGetProperty("answer", out var a) || a.ValueKind != JsonValueKind.Object
                || Plain(a, "body") is not { Length: > 0 } answer)
                continue;

            answers.Add(new GeniusAnswer(question, answer));
        }
        return answers;
    }

    /// <summary>One page of a <c>songs/{id}/comments</c> answer.</summary>
    public static GeniusCommentPage Comments(JsonElement response)
    {
        List<GeniusComment> comments = [];
        if (response.TryGetProperty("comments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in list.EnumerateArray())
            {
                if (Plain(c, "body") is not { Length: > 0 } body)
                    continue;

                var author = c.TryGetProperty("author", out var who) && who.ValueKind == JsonValueKind.Object ? Text(who, "login") : null;
                author ??= c.TryGetProperty("anonymous_author", out var anon) && anon.ValueKind == JsonValueKind.Object ? Text(anon, "name") : null;

                DateTimeOffset? posted = c.TryGetProperty("created_at", out var at) && at.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeSeconds(at.GetInt64())
                    : null;

                comments.Add(new GeniusComment(author ?? "Anonymous", posted, Int(c, "votes_total") ?? 0, body));
            }
        }

        var total = Int(response, "total_count") ?? comments.Count;
        return new GeniusCommentPage(comments, total, Int(response, "next_page"));
    }

    /// <summary>"sampled_in" as the window says it: "Sampled in".</summary>
    public static string ConnectionLabel(string type) => type switch
    {
        "samples" => "Samples",
        "sampled_in" => "Sampled in",
        "interpolates" => "Interpolates",
        "interpolated_by" => "Interpolated by",
        "cover_of" => "Cover of",
        "covered_by" => "Covered by",
        "remix_of" => "Remix of",
        "remixed_by" => "Remixed by",
        "live_version_of" => "Live version of",
        "performed_live_as" => "Performed live as",
        _ => type.Length == 0 ? type : char.ToUpperInvariant(type[0]) + type[1..].Replace('_', ' '),
    };

    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "At", "By", "In", "On", "Of", "For", "And", "With", "To", "From",
    };

    /// <summary>
    /// A credit label Genius's contributors typed ("Mastered At") with its small
    /// words in lower case ("Mastered at"), to sit beside "Recorded at". The rest
    /// is left as typed, so "A&amp;R" stays.
    /// </summary>
    public static string CreditLabel(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < words.Length; i++)
        {
            if (SmallWords.Contains(words[i]))
                words[i] = words[i].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    private static GeniusHit? Hit(JsonElement song)
    {
        if (song.ValueKind != JsonValueKind.Object || Long(song, "id") is not { } id || Text(song, "title") is not { } title)
            return null;

        var artist = song.TryGetProperty("primary_artist", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "name") : null;
        artist ??= Text(song, "artist_names") ?? "";
        return new GeniusHit(id, title, artist, Text(song, "url") ?? "");
    }

    private static void AddCredit(List<GeniusCredit> credits, string label, IReadOnlyList<string> names)
    {
        if (names.Count > 0)
            credits.Add(new GeniusCredit(label, names));
    }

    private static IReadOnlyList<string> Names(JsonElement e, string property) =>
        e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Object ? Text(a, "name") : null).OfType<string>()]
            : [];

    /// <summary>
    /// A <c>{"plain": "..."}</c> text, trimmed; null when absent or empty. A bare
    /// string is taken as it is: a question's own text comes that way, while its
    /// answer comes wrapped (seen on the live site, 2026-11-04).
    /// </summary>
    private static string? Plain(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString()?.Trim() is { Length: > 0 } s ? s : null;
        return v.ValueKind == JsonValueKind.Object ? Text(v, "plain") : null;
    }

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } s
            ? s
            : null;

    private static bool Bool(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    private static long? Long(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? Int(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
