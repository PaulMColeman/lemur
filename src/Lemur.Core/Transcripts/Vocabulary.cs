using System.Text;
using System.Text.RegularExpressions;
using Lemur.Config;

namespace Lemur.Transcripts;

/// <summary>
/// Words and names the transcription gets wrong ("Santavo" for "Zentavo"), from settings.vocabulary. Every new
/// meeting's transcript is fixed before speakers are named and the summary is written, so names, the summary, titles,
/// notes and search all have them right. The summary model is also told how the terms its transcript has are spelled;
/// a Whisper server is given them all as a hint. (Scriberr's Parakeet takes no hints, so with it the fixing is what
/// counts.) A misheard word matches whole words in any case, and may be a phrase ("Zen Tavo").
/// </summary>
public sealed partial class Vocabulary
{
    readonly Dictionary<string, string> _fixes = new(StringComparer.OrdinalIgnoreCase);
    readonly Regex? _pattern;

    public Vocabulary(IEnumerable<VocabularyEntry?>? entries)
    {
        var terms = new List<string>();
        foreach (var entry in entries ?? [])
        {
            var term = Tidy(entry?.Term);
            if (term.Length == 0) continue;
            if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
            foreach (var heard in entry!.HeardAs ?? [])
            {
                var h = Tidy(heard);
                if (Usable(h) && h != term) _fixes.TryAdd(h, term); // the same spelling has nothing to fix
            }
        }
        Terms = terms;
        if (_fixes.Count == 0) return;
        // Every term is matched too, and kept as it is, so a misheard word inside a term is never touched: with
        // Kubernetes for "Cooper", "Alice Cooper" stays. Longest first, so "Zen Tavo" wins over "Zen"; a space matches
        // any run of spaces.
        var alternatives = _fixes.Keys.Concat(terms).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(k => k.Length).Select(k => Regex.Escape(k).Replace("\\ ", @"\s+"));
        _pattern = new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", alternatives)})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>What a match should become: its term, or itself (a term written right, or a match the lookup doesn't
    /// know, which ignoring case the Unicode way can find: the Kelvin sign for K).</summary>
    string Replacement(Match m) => _fixes.TryGetValue(Tidy(m.Value), out var term) ? term : m.Value;

    /// <summary>A misheard spelling must have a letter or digit in it ("-" would rewrite Markdown), and no comma or
    /// semicolon (they separate them in Settings).</summary>
    static bool Usable(string heard) => heard.Any(char.IsLetterOrDigit) && heard.IndexOfAny([',', ';']) < 0;

    public static readonly Vocabulary Empty = new(null);

    /// <summary>Finds a written word whatever it should say: for counting where it is before the right spelling is known.</summary>
    public static Vocabulary Finder(string heard) => new([new VocabularyEntry { Term = "\u0001", HeardAs = [heard] }]);

    /// <summary>Every term, as it should be written.</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>Some term has a misheard spelling to fix.</summary>
    public bool CanFix => _pattern is not null;

    /// <summary>Whether <paramref name="text"/> has a misheard word in it (not one already written right).</summary>
    public bool Mentions(string? text) =>
        _pattern is not null && !string.IsNullOrEmpty(text) && _pattern.Matches(text).Any(m => m.Value != Replacement(m));

    public string Fix(string text) => Fix(text, out _);

    /// <param name="count">How many words were fixed (a word already written right, as "QFN" for "qfn", doesn't count).</param>
    public string Fix(string text, out int count)
    {
        count = 0;
        if (_pattern is null || string.IsNullOrEmpty(text)) return text;
        int n = 0;
        var result = _pattern.Replace(text, m =>
        {
            var replacement = Replacement(m);
            if (m.Value != replacement) n++;
            return replacement;
        });
        count = n;
        return count == 0 ? text : result;
    }

    /// <summary>The transcript with every misheard word fixed; segments with none are kept as they were.</summary>
    public IReadOnlyList<LabelledSegment> Fix(IReadOnlyList<LabelledSegment> segments, out int count)
    {
        count = 0;
        if (_pattern is null) return segments;
        var result = new List<LabelledSegment>(segments.Count);
        foreach (var s in segments)
        {
            var text = Fix(s.Text, out var n);
            count += n;
            result.Add(n == 0 ? s : s with { Text = text });
        }
        return result;
    }

    /// <summary>
    /// For the summary prompt: how the terms in <paramref name="transcript"/> are spelled. Null when it has none. Only those:
    /// told every term as words that "come up in these meetings", the summary model listed terms nobody had said under
    /// Concepts and terms, each with a meaning it made up (Paul, 2026-10-19: all three of his in a meeting with none of them).
    /// </summary>
    /// <param name="transcript">What the model reads, its misheard words already fixed (<see cref="Fix(string)"/>).</param>
    public string? Glossary(string transcript)
    {
        var said = Terms.Where(t => Said(t, transcript)).ToList();
        return said.Count == 0 ? null
            : $"These words and names are in the transcript. Spell them exactly like this: {List(said, 2000)}.";
    }

    /// <summary>The term is in the text as a whole word or phrase, in any case: as <see cref="Fix(string)"/> matches.</summary>
    static bool Said(string term, string text) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term).Replace("\\ ", @"\s+")}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>For a Whisper server's prompt, which reads about 200 tokens: the terms. Null without any.</summary>
    public string? Hint() => Terms.Count == 0 ? null : List(Terms, 600) + ".";

    static string List(IEnumerable<string> terms, int max)
    {
        var sb = new StringBuilder();
        foreach (var t in terms)
        {
            if (sb.Length + t.Length + 2 > max) break;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t);
        }
        return sb.ToString();
    }

    // ---- the list in settings ----

    /// <summary>
    /// Adds a term, and what it's heard as if given, to <paramref name="list"/>. A misheard word listed under another term
    /// moves to this one: the latest correction wins. Correcting a correction — what it wrote is itself a term ("Moravia"),
    /// and the right spelling another — replaces that term: its misheard spellings, and the old spelling, now mean the new
    /// one. (Paul, 2026-10-14: refused as "listed as a correct spelling, and as how … is misheard".)
    /// </summary>
    public static void Add(List<VocabularyEntry> list, string term, string? heard = null)
    {
        term = Tidy(term);
        if (term.Length == 0) throw new ArgumentException("A word needs its correct spelling.");
        var h = heard is null ? "" : Tidy(heard);
        if (h.Length > 0 && !Usable(h)) throw new ArgumentException(Unusable(h));
        if (h.Length > 0)
            foreach (var other in list)
                other.HeardAs.RemoveAll(x => string.Equals(Tidy(x), h, StringComparison.OrdinalIgnoreCase));
        var entry = list.FirstOrDefault(e => string.Equals(Tidy(e.Term), term, StringComparison.OrdinalIgnoreCase));
        if (entry is null) list.Add(entry = new VocabularyEntry { Term = term });
        var replaced = h.Length == 0 ? null
            : list.FirstOrDefault(e => e != entry && string.Equals(Tidy(e.Term), h, StringComparison.OrdinalIgnoreCase));
        if (replaced is not null)
        {
            list.Remove(replaced);
            foreach (var x in replaced.HeardAs.Select(Tidy))
                if (x.Length > 0 && !string.Equals(x, term, StringComparison.OrdinalIgnoreCase) &&
                    !entry.HeardAs.Any(y => string.Equals(Tidy(y), x, StringComparison.OrdinalIgnoreCase)))
                    entry.HeardAs.Add(x);
        }
        if (h.Length > 0 && h != term) entry.HeardAs.Add(h);
    }

    /// <summary>Why <see cref="Add"/> would refuse this correction, or leave the list with a problem; null when it's fine.
    /// Asked before anything is changed, so a refused word doesn't leave meetings fixed and the vocabulary not.</summary>
    public static string? WhyNot(IEnumerable<VocabularyEntry> list, string term, string? heard)
    {
        var copy = list.Select(e => new VocabularyEntry { Term = e.Term, HeardAs = [.. e.HeardAs] }).ToList();
        try
        {
            Add(copy, term, heard);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        return Problems(copy).FirstOrDefault();
    }

    /// <summary>Takes a term, and what it's heard as, out of <paramref name="list"/>.</summary>
    public static bool Remove(List<VocabularyEntry> list, string term) =>
        list.RemoveAll(e => string.Equals(Tidy(e.Term), Tidy(term), StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>The corrections <paramref name="now"/> has that <paramref name="before"/> didn't (to offer them for older notes).</summary>
    public static List<VocabularyEntry> Added(IEnumerable<VocabularyEntry> before, IEnumerable<VocabularyEntry> now)
    {
        var old = new Vocabulary(before);
        var added = new List<VocabularyEntry>();
        foreach (var entry in now)
        {
            var term = Tidy(entry.Term);
            var heard = (entry.HeardAs ?? []).Select(Tidy)
                .Where(h => h.Length > 0 && h != term && !(old._fixes.TryGetValue(h, out var was) && was == term))
                .ToList();
            if (term.Length > 0 && heard.Count > 0) added.Add(new VocabularyEntry { Term = term, HeardAs = heard });
        }
        return added;
    }

    /// <summary>
    /// What's wrong with a list before it's saved: a word that is both a spelling and something misheard, or a misheard
    /// spelling that can't be one (no letters, or a comma).
    /// </summary>
    public static IEnumerable<string> Problems(IEnumerable<VocabularyEntry> list)
    {
        var entries = list.ToList();
        var terms = entries.Select(e => Tidy(e.Term)).Where(t => t.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            foreach (var h in (e.HeardAs ?? []).Select(Tidy).Where(h => h.Length > 0))
            {
                if (!Usable(h)) yield return Unusable(h);
                else if (terms.Contains(h) && !string.Equals(h, Tidy(e.Term), StringComparison.OrdinalIgnoreCase))
                    yield return $"\"{h}\" is listed as a correct spelling, and as how \"{Tidy(e.Term)}\" is misheard.";
            }
    }

    static string Unusable(string heard) => heard.IndexOfAny([',', ';']) >= 0
        ? $"\"{heard}\": a misheard spelling can't have a comma or semicolon in it (they separate them)."
        : $"\"{heard}\": a misheard spelling needs a letter or a digit.";

    /// <summary>One line, single spaces.</summary>
    static string Tidy(string? s) => s is null ? "" : Spaces().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
