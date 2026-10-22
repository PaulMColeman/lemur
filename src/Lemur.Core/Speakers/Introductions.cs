using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Transcripts;

namespace Lemur.Speakers;

public enum ClaimKind { Self, Other }

/// <summary>What the model says it found. Not trusted until <see cref="Introductions.Verify"/> agrees.</summary>
public sealed record NameClaim(string Speaker, string Name, string Quote, double? Time, ClaimKind Kind);

/// <summary>A speaker who said their own name, checked.</summary>
public sealed record Introduction(string Speaker, string Name, string Quote, double Time);

/// <summary>"Thanks, Dana": a guess at who <see cref="ForSpeaker"/> is, only to pre-fill the naming window.</summary>
public sealed record NameHint(string ForSpeaker, string Name, string Quote, double Time);

public interface IChatModel : IDisposable
{
    Task<string> AskAsync(string system, string user, CancellationToken ct);
}

/// <summary>
/// People who say their own name ("hi, it's Dana from finance") are named in that meeting (design 3.9).
/// The summary model finds the candidates; this class decides whether to believe it.
/// </summary>
public static partial class Introductions
{
    public const string Prompt = """
        You find people's names in a meeting transcript. Each line looks like "[00:01:23] Speaker B: text".

        Reply with JSON only, no other text:
        {"names":[{"speaker":"<label exactly as in the transcript>","name":"<the name>","quote":"<words copied exactly from that line>","time":"<hh:mm:ss of that line>","kind":"self or other"}]}

        - "self": the speaker says their OWN name: "I'm Dana", "Dana here", "this is Dana", "my name is Dana",
          "it's Dana from finance".
        - "other": the speaker says SOMEONE ELSE's name: "thanks, Dana", "Sam, over to you". "speaker" is
          still the person talking.
        - Copy the quote exactly; never invent, fix or shorten words inside it.
        - Only people's names: not teams, companies, products, places, or phrases like "I'm good" or "I'm back".
        - If there are none, reply {"names":[]}.
        """;

    static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "good", "fine", "great", "well", "ok", "okay", "back", "here", "there", "muted", "unmuted", "sorry", "ready",
        "done", "sure", "glad", "happy", "excited", "late", "early", "away", "busy", "free", "new", "on", "off", "in",
        "out", "just", "not", "so", "also", "still", "calling", "joining", "leaving", "going", "trying", "everyone",
        "everybody", "all", "team", "sales", "finance", "marketing", "support", "engineering", "it", "this", "that",
        "me", "you", "we", "they", "the", "a", "an", "speaker", "hello", "hi", "hey", "thanks", "thank", "yes", "no",
    };

    /// <summary>Reads the model's reply, tolerating code fences and stray text around the JSON.</summary>
    public static IReadOnlyList<NameClaim> Parse(string reply)
    {
        int a = reply.IndexOf('{'), b = reply.LastIndexOf('}');
        if (a < 0 || b <= a) return [];
        try
        {
            using var doc = JsonDocument.Parse(reply[a..(b + 1)]);
            if (!doc.RootElement.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array) return [];
            var claims = new List<NameClaim>();
            foreach (var n in names.EnumerateArray())
            {
                string? Str(string key) => n.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var speaker = Str("speaker");
                var name = Str("name");
                var quote = Str("quote");
                if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(quote)) continue;
                double? time = n.TryGetProperty("time", out var t)
                    ? t.ValueKind == JsonValueKind.Number ? t.GetDouble() : ParseClock(t.GetString())
                    : null;
                var kind = string.Equals(Str("kind"), "self", StringComparison.OrdinalIgnoreCase) ? ClaimKind.Self : ClaimKind.Other;
                claims.Add(new NameClaim(speaker.Trim(), name.Trim(), quote.Trim(), time, kind));
            }
            return claims;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Keeps only what the transcript backs up (design 3.9): the quote is in that speaker's own lines and
    /// contains the name; the name looks like a name; the speaker isn't the user; nobody claims two names;
    /// no name is claimed by two speakers. "Other" claims become hints for the speaker who talked just before.
    /// </summary>
    public static (IReadOnlyList<Introduction> Accepted, IReadOnlyList<NameHint> Hints) Verify(
        IReadOnlyList<NameClaim> claims, IReadOnlyList<LabelledSegment> segments)
    {
        var turns = TranscriptFormat.MergeTurns(segments);
        bool cased = segments.Any(s => s.Text.Any(char.IsUpper));
        var byLabel = turns.GroupBy(t => t.Speaker).ToDictionary(g => g.Key, g => g.ToList());

        var selfClaims = new List<Introduction>();
        var hints = new List<NameHint>();
        foreach (var c in claims)
        {
            if (!byLabel.TryGetValue(c.Speaker, out var own) || own.Any(t => t.IsMe)) continue;
            var turn = own.FirstOrDefault(t => Normal(t.Text).Contains(Normal(c.Quote)) && Normal(c.Quote).Length > 0);
            if (turn is null) continue;                                  // quote not in their own words: invented
            if (!ContainsWords(Normal(c.Quote), Normal(c.Name))) continue; // the name isn't in the quote
            if (!LooksLikeName(c.Name, turn.Text, cased)) continue;

            if (c.Kind == ClaimKind.Self)
            {
                selfClaims.Add(new Introduction(c.Speaker, c.Name, c.Quote, turn.Start));
            }
            else
            {
                var before = turns.LastOrDefault(t => t.Start < turn.Start && t.Speaker != c.Speaker);
                if (before is not null && !before.IsMe) hints.Add(new NameHint(before.Speaker, c.Name, c.Quote, turn.Start));
            }
        }

        var accepted = selfClaims
            .GroupBy(i => i.Speaker)
            .Where(g => g.Select(i => Normal(i.Name)).Distinct().Count() == 1)        // one speaker, one name
            .Select(g => g.OrderBy(i => i.Time).First())
            .GroupBy(i => Normal(i.Name))
            .Where(g => g.Count() == 1)                                              // one name, one speaker
            .Select(g => g.First())
            .ToList();
        return (accepted, hints);
    }

    static bool LooksLikeName(string name, string sourceText, bool cased)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 3) return false;
        foreach (var w in words)
        {
            if (w.Length < 2 || !w.All(ch => char.IsLetter(ch) || ch is '\'' or '-')) return false;
            if (NotNames.Contains(w)) return false;
            // Where the transcript has capitals at all, the name must be capitalised in the speaker's own words.
            if (cased && !Regex.IsMatch(sourceText, $@"\b{Regex.Escape(char.ToUpperInvariant(w[0]) + w[1..])}\b")) return false;
        }
        return true;
    }

    static string Normal(string s) => Spaces().Replace(NonWord().Replace(s.ToLowerInvariant(), " "), " ").Trim();

    static bool ContainsWords(string haystack, string needle) =>
        needle.Length > 0 && $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);

    static double? ParseClock(string? s) =>
        TimeSpan.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t.TotalSeconds : null;

    [GeneratedRegex(@"[^\p{L}\p{N}']+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>The summary model through an OpenAI-style /chat/completions (settings.summary).</summary>
public sealed class ChatModel : IChatModel
{
    readonly HttpClient _http;
    readonly SummarySettings _settings;

    public ChatModel(SummarySettings settings, string? apiKey, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = Http.Create(handler);
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public static IChatModel? Create(AppSettings settings, ISecretStore secrets, out string? whyNot, HttpMessageHandler? handler = null)
    {
        var s = settings.Summary;
        if (!AppSettings.IsHttpUrl(s.BaseUrl) || string.IsNullOrWhiteSpace(s.Model))
        {
            whyNot = "summary.baseUrl/summary.model not set";
            return null;
        }
        whyNot = null;
        return new ChatModel(s, string.IsNullOrWhiteSpace(s.ApiKeySecret) ? null : secrets.Get(s.ApiKeySecret), handler);
    }

    public async Task<string> AskAsync(string system, string user, CancellationToken ct)
    {
        var body = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            temperature = 0,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await _http.PostAsync($"{_settings.BaseUrl.TrimEnd('/')}/chat/completions", Http.JsonBody(body), cts.Token);
        using var doc = await Http.ReadJson(response, "summary model", cts.Token);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var content = SummaryText.Clean(choice.GetProperty("message").GetProperty("content").GetString() ?? "");
        // A reasoning model can spend the whole budget thinking; that is "couldn't check", not "nobody introduced themselves".
        if (content.Length == 0 && choice.TryGetProperty("finish_reason", out var f) && f.GetString() == "length")
            throw new BackendException($"summary model: it used all {_settings.MaxTokens} of summary.maxTokens before answering");
        return content;
    }

    public void Dispose() => _http.Dispose();
}
