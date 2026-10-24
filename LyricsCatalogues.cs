using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland;

/// The catalogues asked beside LRCLIB. Each of them holds a song by its own number and answers in its own shape, so
/// every one of them is read here the same way: search by the cleaned name and artist, keep only the hits whose words
/// and whose singer are the ones asked for, and take the subtitles of the three closest lengths.
sealed partial class LyricsService
{
    readonly record struct Guess(string Song, string By);

    sealed record Listing(string Id, string Title, string[] Artists, double Duration);

    const string DeezerSite = "https://www.deezer.com/";
    const string DeezerSearchUrl = "https://api.deezer.com/search?limit=10&q=";
    const string DeezerPassUrl = "https://auth.deezer.com/login/anonymous?jo=p&rto=c&i=c";
    const string DeezerLyricUrl = "https://pipe.deezer.com/api";
    const string DeezerLyricQuery = "query TrackLyrics($trackId: String!) { track(trackId: $trackId) { lyrics { synchronizedLines { lrcTimestamp line } } } }";
    const string QqMusicSite = "https://y.qq.com/";
    const string QqMusicSearchUrl = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=10&w=";
    const string QqMusicLyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&songmid=";
    const string KugouSite = "https://www.kugou.com/";
    const string KugouSearchUrl = "https://songsearch.kugou.com/song_search_v2?page=1&pagesize=10&platform=WebFilter&format=json&keyword=";
    const string KugouLyricSearchUrl = "https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&hash=";
    const string KugouLyricUrl = "https://lyrics.kugou.com/download?ver=1&client=pc&fmt=lrc&charset=utf8&id=";
    const string KugouArtistSeparator = "、";
    const string AppleSearchUrl = "https://itunes.apple.com/search?entity=song&limit=10&country=ru&term=";
    const string PaxsenixLyricUrl = "https://lyrics.paxsenix.org/apple-music/lyrics?id=";
    const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    const double MillisecondsPerSecond = 1000;
    const int VersionsPerCatalogue = 3;
    const int FewestLines = 4;

    static readonly Regex Markup = new(@"<[^>]+>", RegexOptions.Compiled);

    static Task<Candidate[]> FromDeezer(string title, string artist, CancellationToken cancel)
    {
        Task<string>? pass = null;
        return FromCatalogue(title, artist, cancel, async query => await SearchDeezer(query, cancel),
            async listing => await DeezerLyric(listing, await (pass ??= DeezerPass(cancel)), cancel));
    }

    static Task<Candidate[]> FromQqMusic(string title, string artist, CancellationToken cancel) =>
        FromCatalogue(title, artist, cancel, async query => await SearchQqMusic(query, cancel), async listing => await QqMusicLyric(listing, cancel));

    static Task<Candidate[]> FromKugou(string title, string artist, CancellationToken cancel) =>
        FromCatalogue(title, artist, cancel, async query => await SearchKugou(query, cancel), async listing => await KugouLyric(listing, cancel));

    /// The last of them, and the one left off until asked for: it reads the Apple catalogue for the song and takes the
    /// words that a fans' site wrote under it, which are often line-for-line where the others only have the plain text.
    static Task<Candidate[]> FromPaxsenix(string title, string artist, CancellationToken cancel) =>
        FromCatalogue(title, artist, cancel, async query => await SearchApple(query, cancel), async listing => await PaxsenixLyric(listing, cancel));

    static async Task<Candidate[]> FromCatalogue(string title, string artist, CancellationToken cancel,
        Func<string, Task<Listing[]>> search, Func<Listing, Task<string>> lyric)
    {
        foreach (Guess guess in Guesses(title, artist))
        {
            if (cancel.IsCancellationRequested) break;
            Listing[] hits = await search(Uri.EscapeDataString(guess.By + " " + guess.Song));
            Listing[] fitting = hits.Where(listing => Fits(listing, guess)).Take(VersionsPerCatalogue).ToArray();
            Candidate?[] found = await Task.WhenAll(fitting.Select(async listing =>
                Timed(WithoutCredits(await lyric(listing), listing), listing.Duration)));
            Candidate[] usable = found.OfType<Candidate>().Where(candidate => candidate.Lines.Length >= FewestLines).ToArray();
            if (usable.Length > 0) return usable;
        }
        return [];
    }

    static IEnumerable<Guess> Guesses(string title, string artist)
    {
        (string song, string by) = Cleaned(title, artist);
        if (by.Length > 0) yield return new Guess(song, by);

        int dash = song.IndexOf(TitleSeparator, StringComparison.Ordinal);
        if (dash > 0) yield return new Guess(song[(dash + TitleSeparator.Length)..].Trim(), song[..dash].Trim());
    }

    /// A catalogue answers a search with whatever it likes, so a hit is only used when its own name and singer are the
    /// ones asked for once the punctuation and the case are gone.
    static bool Fits(Listing listing, Guess guess)
    {
        if (Plain(Noise.Replace(listing.Title, "")) != Plain(guess.Song)) return false;

        string by = Plain(guess.By);
        return listing.Artists.Select(Plain).Any(name => name.Length > 0 && (by.Contains(name) || name.Contains(by)));
    }

    static string Plain(string words) =>
        new(words.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Select(letter => letter == 'ё' ? 'е' : letter).ToArray());

    /// Both the credits a user typed in ahead of the song and the heading some catalogues stamp on its first line are
    /// not words of the song, and would show as the first thing sung.
    static string WithoutCredits(string lrc, Listing listing)
    {
        string heading = Plain(listing.Title);
        return string.Join('\n', lrc.Split('\n').Where(raw =>
        {
            if (Credit.IsMatch(raw)) return false;

            Match stamped = Stamped.Match(raw.Trim());
            string text = stamped.Success ? stamped.Groups[2].Value.Trim() : raw;
            return !(heading.Length > 0 && text.Contains(TitleSeparator) && Plain(text).StartsWith(heading, StringComparison.Ordinal));
        }));
    }

    static async Task<Listing[]> SearchDeezer(string query, CancellationToken cancel)
    {
        using JsonDocument? json = await AskAsync(DeezerSearchUrl + query, DeezerSite, cancel: cancel);
        return Items(json, "data")
            .Select(song => new Listing(Text(song, "id"), Text(song, "title"),
                song.TryGetProperty("artist", out JsonElement by) ? [Text(by, "name")] : [], Number(song, "duration")))
            .ToArray();
    }

    /// Deezer gives its lyrics to an anonymous listener holding a pass it hands out once; without it the words come back
    /// empty, so the first ask for a lyric takes the pass and the rest of the same search reuse it.
    static async Task<string> DeezerPass(CancellationToken cancel)
    {
        using JsonDocument? json = await AskAsync(DeezerPassUrl, DeezerSite, cancel: cancel);
        return json == null ? "" : Text(json.RootElement, "jwt");
    }

    static async Task<string> DeezerLyric(Listing listing, string pass, CancellationToken cancel)
    {
        if (pass.Length == 0) return "";

        string body = JsonSerializer.Serialize(new { operationName = "TrackLyrics", variables = new { trackId = listing.Id }, query = DeezerLyricQuery });
        using JsonDocument? json = await AskAsync(DeezerLyricUrl, DeezerSite, pass, body, cancel);
        return string.Join('\n', Items(json, "data", "track", "lyrics", "synchronizedLines")
            .Select(line => Text(line, "lrcTimestamp") + Text(line, "line")));
    }

    static async Task<Listing[]> SearchQqMusic(string query, CancellationToken cancel)
    {
        using JsonDocument? json = await AskAsync(QqMusicSearchUrl + query, QqMusicSite, cancel: cancel);
        return Items(json, "data", "song", "list")
            .Select(song => new Listing(Text(song, "songmid"), Text(song, "songname"),
                Items(song, "singer").Select(by => Text(by, "name")).ToArray(), Number(song, "interval")))
            .ToArray();
    }

    static async Task<string> QqMusicLyric(Listing listing, CancellationToken cancel)
    {
        using JsonDocument? json = await AskAsync(QqMusicLyricUrl + listing.Id, QqMusicSite, cancel: cancel);
        return json == null ? "" : WebUtility.HtmlDecode(Decoded(Text(json.RootElement, "lyric")));
    }

    static async Task<Listing[]> SearchKugou(string query, CancellationToken cancel)
    {
        using JsonDocument? json = await AskAsync(KugouSearchUrl + query, KugouSite, cancel: cancel);
        return Items(json, "data", "lists")
            .Select(song => new Listing(Text(song, "FileHash"), Markup.Replace(Text(song, "SongName"), ""),
                Markup.Replace(Text(song, "SingerName"), "").Split(KugouArtistSeparator), Number(song, "Duration")))
            .ToArray();
    }

    /// Kugou keeps its words in a script of its own and hands them out by the hash of the file: the hash asks which
    /// versions exist, and the first of them is fetched as plain text.
    static async Task<string> KugouLyric(Listing listing, CancellationToken cancel)
    {
        using JsonDocument? versions = await AskAsync(KugouLyricSearchUrl + listing.Id, KugouSite, cancel: cancel);
        JsonElement version = Items(versions, "candidates").FirstOrDefault();
        if (version.ValueKind != JsonValueKind.Object) return "";

        string address = KugouLyricUrl + Uri.EscapeDataString(Text(version, "id")) + "&accesskey=" + Uri.EscapeDataString(Text(version, "accesskey"));
        using JsonDocument? json = await AskAsync(address, KugouSite, cancel: cancel);
        return json == null ? "" : Decoded(Text(json.RootElement, "content"));
    }

    static async Task<Listing[]> SearchApple(string query, CancellationToken cancel)
    {
        using JsonDocument? json = await AskAsync(AppleSearchUrl + query, cancel: cancel);
        return Items(json, "results")
            .Select(song => new Listing(Text(song, "trackId"), Text(song, "trackName"), [Text(song, "artistName")],
                Number(song, "trackTimeMillis") / MillisecondsPerSecond))
            .ToArray();
    }

    static async Task<string> PaxsenixLyric(Listing listing, CancellationToken cancel)
    {
        using JsonDocument? json = await AskAsync(PaxsenixLyricUrl + listing.Id, cancel: cancel);
        return json == null ? "" : Text(json.RootElement, "lrc");
    }

    static string Decoded(string base64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)).TrimStart('﻿'); }
        catch (FormatException) { return ""; }
    }

    static IEnumerable<JsonElement> Items(JsonDocument? json, params string[] path) => json == null ? [] : Items(json.RootElement, path).ToArray();

    static IEnumerable<JsonElement> Items(JsonElement from, params string[] path)
    {
        foreach (string name in path)
            if (from.ValueKind != JsonValueKind.Object || !from.TryGetProperty(name, out from)) return [];
        return from.ValueKind == JsonValueKind.Array ? from.EnumerateArray() : [];
    }
}
