using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>
/// The meeting as a file a program can read (docs/2026-10-15-meeting-export-design.md §3.1): "&lt;date&gt; &lt;title&gt;.json"
/// beside the note's .md, .html and .mp3, in the "lemur.meeting/1" format — the meeting's facts, who was heard, the summary's
/// sections, its action items and which are the user's, links back to the note, and the transcript unless the user left it
/// out (<see cref="MeetingFileSettings.IncludeTranscript"/>). Made from Lemur's own
/// files (meta.json, summary.md, labelled.json, identified.json, state.json), never from the note, so a note the user edited
/// still gets the meeting as Lemur knows it. Written with the note and again whenever the note is (naming, Fix a word,
/// remake) or renamed; the queue's pass makes it for older meetings, and for any whose file couldn't be written. A file
/// that can't be written never fails the note: it is logged and tried again.
/// </summary>
public static partial class MeetingFile
{
    public const string FormatName = "lemur.meeting/1";

    /// <summary>What the file holds and how; raised when that changes, so the queue's pass makes every meeting's file again.</summary>
    const int Version = 1;

    /// <summary>How long the queue's pass leaves a file that couldn't be written before trying again.</summary>
    static readonly TimeSpan RetryWait = TimeSpan.FromMinutes(15);

    /// <param name="Seconds">How long they talked.</param>
    /// <param name="Named">Named for sure: not "Speaker E", not a guess like "Dana?".</param>
    public sealed record Person(string Name, bool Me, int Seconds, bool Named);

    /// <param name="Id">"&lt;recording&gt;/&lt;number&gt;": the same item keeps it when the summary is read again (<see cref="ProcessingState.ActionItemNumbers"/>).</param>
    /// <param name="Due">yyyy-MM-dd, when <paramref name="DueText"/> pins a day down.</param>
    /// <param name="At">Seconds into the meeting where it was agreed.</param>
    /// <param name="Link">Opens the note there (<see cref="NoteLinks"/>).</param>
    public sealed record Item(string Id, string Text, string? Owner, bool OwnerIsMe, string? Due, string? DueText, int? At,
        string? Link, bool Done);

    /// <summary>The note's files, by name beside this one; <paramref name="Audio"/> is null without an MP3.</summary>
    public sealed record Notes(string Markdown, string Page, string? Audio);

    /// <summary>A line of the transcript as the note shows it: one speaker's turn.</summary>
    public sealed record Line(double Start, double End, string Speaker, bool Me, string Text);

    /// <summary>The file, in its order.</summary>
    public sealed record Content
    {
        public string Format { get; init; } = FormatName;

        /// <summary>The recording folder's name ("20261014-093000"), which never changes.</summary>
        public string Id { get; init; } = "";

        /// <summary>A hash of everything else in the file, so a receiver can tell an update from a repeat.</summary>
        public string? Revision { get; init; }

        /// <summary>When that revision was made: kept while the revision stays the same.</summary>
        public DateTimeOffset? RevisedAt { get; init; }

        public string Title { get; init; } = "";
        public string? Description { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset EndedAt { get; init; }
        public double DurationSeconds { get; init; }
        public string Source { get; init; } = "";

        /// <summary>The user's display name.</summary>
        public string RecordedBy { get; init; } = "";

        /// <summary>Everyone heard: the user first, then by how long they talked; "Other audio" isn't anyone.</summary>
        public IReadOnlyList<Person> People { get; init; } = [];

        /// <summary>The summary's Summary section.</summary>
        public string? Summary { get; init; }

        /// <summary>The summary has action items in the prompt's form, so their owners can be trusted (<see cref="ActionItemList.Found"/>).</summary>
        public bool ActionItemsFound { get; init; }

        public IReadOnlyList<Item> ActionItems { get; init; } = [];

        /// <summary>Opens the meeting's note at the top.</summary>
        public string? Link { get; init; }

        public IReadOnlyList<string> Decisions { get; init; } = [];
        public IReadOnlyList<string> OpenQuestions { get; init; } = [];
        public Notes? Notes { get; init; }

        /// <summary>summary.md whole, for a receiver that reads the sections itself.</summary>
        public string SummaryMarkdown { get; init; } = "";

        /// <summary>Null, and not in the file, when it was left out.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<Line>? Transcript { get; init; }

        /// <summary>What the user chose to leave out ("transcript"), so a receiver can tell that from a meeting without it; null for nothing.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? LeftOut { get; init; }
    }

    /// <summary>The name <see cref="Content.LeftOut"/> gives the transcript.</summary>
    public const string TranscriptPart = "transcript";

    /// <summary>The meeting file of a note: its name with ".json".</summary>
    public static string PathFor(string notePath) => Path.ChangeExtension(notePath, ".json");

    /// <summary>
    /// The meeting as its file says it, from the recording's own files. Action items new to it are numbered in
    /// <paramref name="state"/>'s table, which the caller saves; the revision is left to <see cref="Write"/>.
    /// </summary>
    /// <param name="vocabulary">The words the meeting may have had fixed (settings.vocabulary, and a word just fixed), so an
    /// item in which one was fixed keeps its number.</param>
    /// <param name="transcript">Put the transcript in (<see cref="MeetingFileSettings.IncludeTranscript"/>); it is still read,
    /// since the people heard and the items' owners come from it.</param>
    /// <exception cref="InvalidDataException">There's no summary or transcript to make it from.</exception>
    public static Content Build(RecordingFolder rec, ProcessingState state, string displayName, Vocabulary? vocabulary = null,
        CultureInfo? culture = null, bool transcript = true)
    {
        var note = state.NotePath ?? throw new InvalidOperationException("its notes aren't made yet");
        var meta = rec.LoadMeta();
        if (!File.Exists(rec.SummaryFile)) throw new InvalidDataException("summary.md missing");
        var summary = File.ReadAllText(rec.SummaryFile);
        var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile) ?? throw new InvalidDataException("labelled.json missing");
        var identified = rec.Read<IdentifiedResult>(rec.IdentifiedFile);
        var named = Naming.Apply(labelled, identified, displayName); // as the note shows it

        // Lines labelled before the display name changed carry the old one: that is the user too.
        var me = named.Where(s => s.IsMe).Select(s => s.Speaker).Prepend(displayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var others = named.Where(s => !s.IsMe && s.Speaker != Naming.NotAPersonLabel).Select(s => s.Speaker).Distinct().ToList();
        var day = DateOnly.FromDateTime(meta.StartedAt.ToLocalTime().DateTime);
        var read = ActionItems.Read(summary, day, meta.DurationSeconds, me, others, culture);
        var names = me.Concat(others).Concat(identified?.Speakers.SelectMany(s => new[] { s.Name, s.Label }).OfType<string>() ?? []).ToList();
        var numbers = Number(read.Items, state, names, vocabulary ?? Vocabulary.Empty);
        var items = read.Items.Select((item, i) => new Item($"{rec.Name}/{numbers[i]}", item.Text, item.Owner, item.OwnerIsMe,
            item.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), item.DueText, item.At, Link(rec.Name, item.At), item.Done)).ToList();

        var sure = identified is null ? null : Naming.People(identified);
        var people = named
            .Where(s => !string.IsNullOrWhiteSpace(s.Text) && s.Speaker != Naming.NotAPersonLabel)
            .GroupBy(s => s.Speaker)
            .Select(g => (Name: g.Key, Me: g.First().IsMe, Seconds: g.Sum(s => Math.Max(0, s.End - s.Start))))
            .OrderByDescending(p => p.Me).ThenByDescending(p => p.Seconds)
            .Select(p => new Person(p.Name, p.Me, (int)Math.Round(p.Seconds),
                p.Me || (sure is null ? !Unnamed().IsMatch(p.Name) : sure.Contains(p.Name, StringComparer.Ordinal))))
            .ToList();
        var audio = NoteWriter.AudioPath(note);

        return new Content
        {
            Id = rec.Name,
            Title = string.IsNullOrWhiteSpace(meta.Title) ? "Meeting" : meta.Title, // as the note's heading says
            Description = string.IsNullOrWhiteSpace(meta.Description) ? null : meta.Description,
            StartedAt = meta.StartedAt,
            EndedAt = meta.EndedAt == default ? meta.StartedAt.AddSeconds(meta.DurationSeconds) : meta.EndedAt,
            DurationSeconds = meta.DurationSeconds,
            Source = meta.Source,
            RecordedBy = displayName,
            People = people,
            Summary = SummarySections.Text(summary, "summary"),
            ActionItemsFound = read.Found,
            ActionItems = items,
            Link = Link(rec.Name, null),
            Decisions = SummarySections.Bullets(summary, "decisions", "decision"),
            OpenQuestions = SummarySections.Bullets(summary, "open questions", "open question"),
            Notes = new Notes(Path.GetFileName(note), Path.GetFileName(NoteWriter.HtmlPath(note)), File.Exists(audio) ? Path.GetFileName(audio) : null),
            SummaryMarkdown = summary,
            Transcript = transcript
                ? TranscriptFormat.MergeTurns(named).Select(t => new Line(Math.Round(t.Start, 1), Math.Round(t.End, 1), t.Speaker, t.IsMe, t.Text)).ToList()
                : null,
            LeftOut = transcript ? null : [TranscriptPart],
        };
    }

    /// <summary>A link to the note, at a moment; none for a folder whose name isn't a recording's (one processed from elsewhere).</summary>
    static string? Link(string recording, int? at)
    {
        try { return NoteLinks.For(recording, at); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Writes a finished meeting's file beside its note, or into <paramref name="folder"/> (llmmr export), under the meeting's
    /// lock. The revision is a hash of the content, and its time is kept from the file already there (or, for an export, the
    /// one beside the note) while it is the same; a file that would come out the same isn't touched. Written beside and
    /// moved over, so a reader never sees half of it.
    /// </summary>
    /// <returns>The file's path.</returns>
    /// <exception cref="InvalidOperationException">Its notes aren't made yet, or (without a folder) its note is gone.</exception>
    public static string Write(RecordingFolder rec, string displayName, string? folder = null, Vocabulary? vocabulary = null,
        CultureInfo? culture = null, DateTimeOffset? now = null, bool transcript = true)
    {
        using (RecordingFolder.Lock())
        {
            var state = rec.LoadState();
            if (state.Stage != Stage.Done || state.NotePath is not { } note) throw new InvalidOperationException("its notes aren't made yet");
            if (folder is null && !File.Exists(note)) throw new InvalidOperationException("its note isn't there any more"); // deleted or moved
            var stamp = Stamp(rec, note, displayName, culture, transcript); // before reading them: a change made meanwhile is caught next time
            var content = Build(rec, state, displayName, vocabulary, culture, transcript);
            rec.UpdateState(s => (s.ActionItemNumbers, s.NextActionItem) = (state.ActionItemNumbers, state.NextActionItem));

            var path = folder is null ? PathFor(note) : Path.Combine(folder, Path.GetFileName(PathFor(note)));
            var revision = Revision(content);
            var (had, at) = Existing(path);
            if (had != revision && folder is not null) (had, at) = Existing(PathFor(note)); // an export says when its revision was made too
            var time = now ?? DateTimeOffset.Now;
            content = content with { Revision = revision, RevisedAt = had == revision && at is not null ? at : time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond)) };
            if (Save(path, content))
                Log.Info($"{rec.Name}: meeting file written ({content.ActionItems.Count} action {(content.ActionItems.Count == 1 ? "item" : "items")}, " +
                         $"{content.ActionItems.Count(i => i.OwnerIsMe)} yours; revision {revision[..4]})");
            if (folder is null) rec.UpdateState(s => (s.MeetingFileStamp, s.MeetingFileRetryAt) = (stamp, null));
            return path;
        }
    }

    /// <summary>
    /// <see cref="Write"/> for whatever just changed a finished meeting's note, which mustn't fail because of it: a failure is
    /// logged (the meeting and the exception's type only, since paths hold titles and a parser's message the text it read).
    /// A file open elsewhere or read-only is tried again by the queue's pass after a while; anything else would fail the same
    /// way again, so it waits until the meeting's files change: a summary or transcript that is missing or unreadable, or a bug.
    /// </summary>
    /// <returns>Whether the file is up to date now; false too for a meeting without a note to put it beside.</returns>
    public static bool TryWrite(RecordingFolder rec, string displayName, Vocabulary? vocabulary = null, CultureInfo? culture = null,
        DateTimeOffset? now = null, bool transcript = true)
    {
        string? stamp = null;
        try
        {
            var state = rec.LoadState();
            if (state.Stage != Stage.Done || state.NotePath is not { } note || !File.Exists(note)) return false;
            stamp = Stamp(rec, note, displayName, culture, transcript);
            Write(rec, displayName, vocabulary: vocabulary, culture: culture, now: now, transcript: transcript);
            return true;
        }
        catch (Exception ex)
        {
            bool passing = ex is IOException or UnauthorizedAccessException or OperationCanceledException;
            if (passing) Log.Warn($"{rec.Name}: couldn't write its meeting file ({ex.GetType().Name}); it will be tried again");
            else if (ex is InvalidDataException or JsonException)
                Log.Warn($"{rec.Name}: no meeting file, its files are missing or unreadable ({ex.GetType().Name}); tried again when they change");
            else Log.Error($"{rec.Name}: couldn't make its meeting file, a bug ({Log.TypeAndCode(ex)}); tried again when its files change");
            try
            {
                var retry = (now ?? DateTimeOffset.Now) + RetryWait;
                rec.UpdateState(s =>
                {
                    if (!passing && stamp is not null) s.MeetingFileStamp = Unmakeable(stamp);
                    else s.MeetingFileRetryAt = retry;
                });
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException or JsonException)
            {
                // Left as it was: the pass tries again at once rather than later.
            }
            return false;
        }
    }

    /// <summary>
    /// The queue's pass: up to <paramref name="max"/> finished meetings whose file is missing or older than what it is made
    /// from (meetings from before there were meeting files, a rename in the Recordings window, a description added, a write
    /// that failed, the transcript put in or left out). Only file times are looked at for the rest, so a pass over every
    /// meeting stays cheap. A meeting whose note is gone gets none; one whose recording folder is gone isn't seen, and keeps
    /// the file it had.
    /// </summary>
    /// <returns>How many files were made or brought up to date.</returns>
    public static int RefreshAll(string root, string displayName, Vocabulary? vocabulary = null, int max = int.MaxValue,
        CultureInfo? culture = null, DateTimeOffset? now = null, bool transcript = true)
    {
        var time = now ?? DateTimeOffset.Now;
        int tried = 0, done = 0;
        foreach (var rec in RecordingFolder.All(root))
        {
            if (tried >= max) break;
            try
            {
                var state = rec.LoadState();
                if (state.Stage != Stage.Done || state.NotePath is not { } note || !File.Exists(note) || state.MeetingFileRetryAt > time) continue;
                var stamp = Stamp(rec, note, displayName, culture, transcript);
                if (state.MeetingFileStamp == Unmakeable(stamp) || (state.MeetingFileStamp == stamp && File.Exists(PathFor(note)))) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            tried++;
            if (TryWrite(rec, displayName, vocabulary, culture, time, transcript)) done++;
        }
        return done;
    }

    /// <summary>
    /// What a meeting's file is made from, in a few characters: the newest write time of its inputs, the note's path (renamed),
    /// whether its MP3 is there, the display name (recordedBy, and which items are the user's), the culture due dates are
    /// read in, whether the transcript is left out, and <see cref="Version"/>. Saved with the file; when it differs, the file
    /// is out of date. The vocabulary isn't in it: it only matches items to their numbers, and a summary that didn't change
    /// gives the same file.
    /// </summary>
    public static string Stamp(RecordingFolder rec, string notePath, string displayName, CultureInfo? culture = null, bool transcript = true)
    {
        long newest = new[] { rec.MetaFile, rec.SummaryFile, rec.LabelledFile, rec.IdentifiedFile }
            .Select(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0).Max();
        var made = $"{Version}|{newest}|{notePath}|{File.Exists(NoteWriter.AudioPath(notePath))}|{displayName}|{(culture ?? CultureInfo.CurrentCulture).Name}" +
                   (transcript ? "" : "|no transcript"); // the same as before the choice existed, so those files aren't made again
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(made)))[..16].ToLowerInvariant();
    }

    /// <summary>Saved instead of the stamp for a meeting without the files to make one from, until they change.</summary>
    static string Unmakeable(string stamp) => "none:" + stamp;

    /// <summary>
    /// Lemur's JSON settings, but writing text as it is ("I'll", "Zoë") rather than as \u escapes: the file is read by
    /// people and by programs, and neither puts it inside a web page, which is what the escapes guard against.
    /// </summary>
    static readonly JsonSerializerOptions FileJson = new(Json.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static string Revision(Content content) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content with { Revision = null, RevisedAt = null }, FileJson)))[..16]
            .ToLowerInvariant();

    /// <summary>The revision and its time in the file already there; nulls if there is none, or it isn't one.</summary>
    static (string? Revision, DateTimeOffset? At) Existing(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            var revision = root.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            DateTimeOffset? at = root.TryGetProperty("revisedAt", out var t) && t.ValueKind == JsonValueKind.String && t.TryGetDateTimeOffset(out var v)
                ? v : null;
            return (revision, at);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Written beside and moved over; a file that is already exactly this is left alone, for whatever watches the folder.</summary>
    /// <returns>Whether the file changed.</returns>
    static bool Save(string path, Content content)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content, FileJson);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;
        var tmp = path + ".lemur-tmp";
        File.WriteAllBytes(tmp, bytes);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return true;
    }

    /// <summary>
    /// Each item's number: that of the item numbered before whose text is the same once both are made comparable with what
    /// the meeting knows now (<see cref="Comparable"/>), and which is the same occurrence of it (two "send the deck" are 1
    /// and 2, in the order they were numbered); else the next free one. Naming a speaker or fixing a word changes both
    /// sides alike; anything else, a remake's rewording, makes a new item. One edge is left: two items that differ only by
    /// a label not yet named ("call Speaker B", "call Dana"), one of them dropped by a remake, are the same once that label
    /// is named Dana, and the one left can take the other's number.
    /// </summary>
    /// <param name="names">Everyone the meeting names now, the user too.</param>
    static List<int> Number(IReadOnlyList<ActionItem> items, ProcessingState state, IReadOnlyCollection<string> names, Vocabulary vocabulary)
    {
        var table = state.ActionItemNumbers;
        var people = Names(names);
        var counted = new Dictionary<string, int>();
        (string, int) Key(string text)
        {
            var key = Comparable(text, people, vocabulary);
            return (key, counted[key] = counted.GetValueOrDefault(key) + 1);
        }

        var before = new Dictionary<(string, int), ActionItemNumber>();
        foreach (var n in table.OrderBy(n => n.Number)) before[Key(n.Text)] = n;
        counted.Clear();
        var numbers = new List<int>(items.Count);
        foreach (var item in items)
        {
            if (before.Remove(Key(item.Text), out var was))
            {
                // A word the summary has fixed now is fixed in what is kept, for a later write without it in the vocabulary
                // (Fix a word, not remembered); one it still has misheard (a word added but not fixed in it) is kept as it is.
                if (!vocabulary.Mentions(item.Text)) was.Text = Kept(vocabulary.Fix(was.Text));
                numbers.Add(was.Number);
                continue;
            }
            int next = Math.Max(state.NextActionItem, table.Count == 0 ? 1 : table.Max(n => n.Number) + 1);
            table.Add(new ActionItemNumber { Text = Kept(item.Text), Number = next });
            state.NextActionItem = next + 1;
            numbers.Add(next);
        }
        return numbers;
    }

    /// <summary>An item's text as the table keeps it (<see cref="ActionItemNumber.Text"/>).</summary>
    static string Kept(string text) => Spaces().Replace(text.ToLowerInvariant(), " ").Trim();

    /// <summary>
    /// An item's text as it is compared, the kept one and the new one alike: the vocabulary's fixes made, lower case,
    /// without speaker labels ("Speaker B") or the names of the meeting's people as it has them now (whole, and first
    /// names), and only letters and digits left, so "Zen Tavo" fixed into "Zentavo" compares the same. Naming Speaker B
    /// "Dana Whitfield" turns "send Speaker B the deck" into "send Dana Whitfield the deck"; both come out "sendthedeck".
    /// </summary>
    static string Comparable(string text, Regex? people, Vocabulary vocabulary)
    {
        var s = SpeakerLabel().Replace(vocabulary.Fix(text).ToLowerInvariant(), " ");
        if (people is not null) s = people.Replace(s, " ");
        return NotLetterOrDigit().Replace(s, "");
    }

    /// <summary>Matches any of the names (lower case, whole words, longest first), and their first words; null for none.</summary>
    static Regex? Names(IEnumerable<string> names)
    {
        var all = names.Select(n => Spaces().Replace(n.Trim().TrimEnd('?').Trim().ToLowerInvariant(), " "))
            .Where(n => n.Length > 0 && !SpeakerLabel().IsMatch(n))
            .SelectMany(n => n.Contains(' ') ? new[] { n, n[..n.IndexOf(' ')] } : new[] { n })
            .Distinct().OrderByDescending(n => n.Length).ToList();
        return all.Count == 0 ? null
            : new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", all.Select(n => Regex.Escape(n).Replace("\\ ", @"\s+")))})(?![\p{{L}}\p{{N}}])",
                RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])speaker\s+[a-z]{1,2}(?![\p{L}\p{N}])")]
    private static partial Regex SpeakerLabel();

    [GeneratedRegex(@"[^\p{L}\p{N}]")]
    private static partial Regex NotLetterOrDigit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // A label Lemur made up ("Speaker E") or a guess ("Dana?"), as the note page dims them.
    [GeneratedRegex(@"^Speaker [A-Z]{1,2}$|\?$")]
    private static partial Regex Unnamed();
}
