using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>
/// The note as a self-contained web page, for people to read: summary rendered, transcript coloured by
/// speaker. No external resources, and the only script is the audio player's (<see cref="PlayerScript"/>),
/// present only when the meeting's MP3 sits beside the page — the CSP allows that exact script by its hash
/// and nothing else, so nothing the transcript or the model's summary contains can run or phone home.
/// </summary>
public static partial class NoteHtml
{
    // Raw HTML in the summary is escaped rather than passed through; no generic-attributes extension.
    static readonly MarkdownPipeline SummaryPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().UseListExtras()
        .DisableHtml()
        .Build();

    const int SpeakerColours = 6;

    /// <summary>The address the tray app registers so this static page can open its naming window.</summary>
    public const string NameLinkScheme = "lemur";

    /// <summary>What pages written before 1.0 (as Teams Recorder) link with; Lemur answers it too.</summary>
    public const string LegacyNameLinkScheme = "teamsrecorder";

    /// <summary>
    /// The page's layout, written into it: pages made with an older one are made again from the meeting's files
    /// (<see cref="NotePages"/>). 2: the attendees list. 3: opens at a moment (#t=, lemur://note/ links), and the
    /// summary's action items play from when they were agreed.
    /// </summary>
    public const int PageVersion = 3;

    const string Csp = "default-src 'none'; style-src 'unsafe-inline'";

    /// <summary>With audio: the MP3 may load from beside the page ('self' over http; file: for a page opened from disk) and the player may run.</summary>
    static string CspWithAudio => $"{Csp}; media-src 'self' file:; script-src '{ScriptHash(PlayerScript)}'";

    /// <param name="audio">The meeting's MP3, by file name beside the page; adds a player and makes the transcript clickable.</param>
    public static string Render(RecordingMeta meta, string summaryMarkdown, IReadOnlyList<LabelledSegment> segments,
        string backendKind, string? webUrl, IReadOnlyList<string>? people = null, string? recording = null, string? audio = null)
    {
        var local = meta.StartedAt.ToLocalTime();
        var title = string.IsNullOrWhiteSpace(meta.Title) ? "Meeting" : meta.Title;
        var turns = Turns(segments);
        var others = turns.Where(t => !t[0].IsMe).Select(t => t[0].Speaker).Distinct().ToList();
        var src = audio is null ? null : E(Uri.EscapeDataString(audio));

        var sb = new StringBuilder();
        sb.Append(Head.Replace("{csp}", audio is null ? Csp : CspWithAudio).Replace("{title}", E(title))
            .Replace("{generator}", $"Lemur note page {PageVersion}"));
        sb.AppendLine("<header>");
        sb.AppendLine($"<h1>{E(title)}</h1>");
        sb.Append($"<p class=\"meta\">{E(local.ToString("dddd d MMMM yyyy, HH:mm"))} · {TranscriptFormat.Clock(meta.DurationSeconds)} · {E(meta.Source)}");
        sb.AppendLine(webUrl is null ? "</p>" : $" · <a href=\"{E(webUrl)}\">open in {E(backendKind)}</a></p>");
        Attendees(sb, segments, others, people, recording);
        sb.AppendLine("</header>");
        if (src is not null)
        {
            sb.AppendLine("<div class=\"player\" id=\"player\">");
            sb.AppendLine($"<audio id=\"audio\" controls preload=\"metadata\" src=\"{src}\"></audio>");
            sb.AppendLine("<p class=\"hint\">Click any line of the transcript to hear it.</p>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("<section class=\"summary\">");
        sb.AppendLine(Summary(summaryMarkdown, audio is null ? null : Uri.EscapeDataString(audio), meta.DurationSeconds).Trim());
        sb.AppendLine("</section>");
        sb.AppendLine("<section>");
        sb.AppendLine("<h2>Transcript</h2>");
        sb.AppendLine(src is null ? "<ol class=\"transcript\">" : "<ol class=\"transcript playable\">");
        foreach (var turn in turns)
        {
            var first = turn[0];
            var cls = first.IsMe ? "me" : $"s{others.IndexOf(first.Speaker) % SpeakerColours}";
            var clock = TranscriptFormat.Clock(first.Start);
            if (src is null)
            {
                var text = string.Join(" ", turn.Select(s => s.Text.Trim()));
                sb.AppendLine($"<li class=\"{cls}\"><time>{clock}</time><b>{E(first.Speaker)}</b><p>{E(text)}</p></li>");
                continue;
            }
            // Each segment is its own clickable line; the time links straight into the MP3 (#t=) if scripts are off.
            var lines = string.Join(" ", turn.Select(s => $"<span data-t=\"{Seconds(s.Start)}\" data-e=\"{Seconds(s.End)}\">{E(s.Text.Trim())}</span>"));
            sb.AppendLine($"<li class=\"{cls}\"><time><a href=\"{src}#t={Seconds(first.Start)}\">{clock}</a></time><b>{E(first.Speaker)}</b><p>{lines}</p></li>");
        }
        sb.AppendLine("</ol>");
        sb.AppendLine("</section>");
        sb.AppendLine("</main>");
        if (src is not null) sb.Append("<script>").Append(PlayerScript).AppendLine("</script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// Everyone heard in the meeting, as chips in the colours of their transcript lines: you first, then the others by how
    /// long they talked, each with that time. A voice nobody has named for sure ("Speaker E", "Dana?") is dimmed, and the
    /// link to name them follows. "Other audio" (a video, the TV) isn't an attendee.
    /// </summary>
    /// <param name="others">The other speakers in order of first appearance, which is how their colours are given.</param>
    /// <param name="people">Those named for sure; null when unknown, and then labels decide ("Speaker E", "Dana?").</param>
    static void Attendees(StringBuilder sb, IReadOnlyList<LabelledSegment> segments, List<string> others,
        IReadOnlyList<string>? people, string? recording)
    {
        var heard = segments
            .Where(s => !string.IsNullOrWhiteSpace(s.Text) && s.Speaker != Speakers.Naming.NotAPersonLabel)
            .GroupBy(s => s.Speaker)
            .Select(g => (Name: g.Key, IsMe: g.First().IsMe, Seconds: g.Sum(s => Math.Max(0, s.End - s.Start))))
            .OrderByDescending(a => a.IsMe).ThenByDescending(a => a.Seconds)
            .ToList();
        if (heard.Count > 0)
        {
            sb.AppendLine("<section class=\"attendees\" aria-labelledby=\"attendees\">");
            sb.AppendLine($"<h2 id=\"attendees\">Attendees <span class=\"n\">{heard.Count}</span></h2>");
            sb.AppendLine("<ul>");
            foreach (var a in heard)
            {
                var colour = a.IsMe ? "me" : $"s{Math.Max(0, others.IndexOf(a.Name)) % SpeakerColours}";
                bool named = a.IsMe || (people is null ? !Unnamed().IsMatch(a.Name) : people.Contains(a.Name, StringComparer.Ordinal));
                var about = (a.IsMe ? "you · " : "") + TalkTime(a.Seconds); // fixed words and numbers: nothing to escape
                sb.AppendLine($"<li class=\"{colour}{(named ? "" : " unnamed")}\"><b>{E(a.Name)}</b><span>{about}</span></li>");
            }
            sb.AppendLine("</ul>");
        }
        if (recording is not null)
            sb.AppendLine($"<p class=\"meta hint\"><a href=\"{NameLinkScheme}://name/{Uri.EscapeDataString(recording)}\">Name speakers</a> " +
                          "(opens Lemur on this PC)</p>");
        if (heard.Count > 0) sb.AppendLine("</section>");
    }

    /// <summary>
    /// The summary as HTML. With the recording beside the page, a time ending a list item — the prompt puts one after
    /// each action item, when it was agreed: "- [ ] Paul - send the deck - Fri 2026-10-16 [00:12:34]" — becomes a link
    /// that plays the meeting from then, read as the meeting file reads it (<see cref="ActionItems.TrailingTime"/>). Done
    /// on Markdig's tree, so the summary's own text is escaped as ever.
    /// </summary>
    /// <param name="audioUrl">The MP3's file name, escaped for a URL; null leaves times as written.</param>
    static string Summary(string markdown, string? audioUrl, double duration)
    {
        var doc = Markdown.Parse(markdown.Trim(), SummaryPipeline);
        if (audioUrl is not null)
            foreach (var item in doc.Descendants<ListItemBlock>())
                if (item.Count > 0 && item[0] is ParagraphBlock { Inline: { } inline }) LinkTime(inline, audioUrl, duration);
        return doc.ToHtml(SummaryPipeline);
    }

    static void LinkTime(ContainerInline inline, string audioUrl, double duration)
    {
        // Markdig leaves an unmatched "[…]" as several literals ("[", "00:12:34", "]"): the run at the end, read together.
        var tail = new List<LiteralInline>();
        for (var node = inline.LastChild; node is LiteralInline literal; node = node.PreviousSibling) tail.Insert(0, literal);
        var text = string.Concat(tail.Select(l => l.Content.ToString()));
        // Past the end it is the model's mistake, left as it wrote it.
        if (ActionItems.TrailingTime(text) is not { } time || !time.Within(duration)) return;
        foreach (var literal in tail) literal.Remove();
        if (time.Index > 0) inline.AppendChild(new LiteralInline(text[..time.Index]));
        // Straight into the MP3 if scripts are off, as the transcript's times; the player plays it in the page.
        var at = time.Seconds.ToString(CultureInfo.InvariantCulture);
        var link = new LinkInline($"{audioUrl}#t={at}", "Play from here");
        link.AppendChild(new LiteralInline(time.Clock));
        link.GetAttributes().AddClass("moment");
        link.GetAttributes().AddProperty("data-t", at);
        inline.AppendChild(link);
    }

    static string TalkTime(double seconds) => seconds < 60 ? $"{Math.Max(1, Math.Round(seconds)):0} s" : $"{Math.Round(seconds / 60):0} min";

    // A label Lemur made up ("Speaker E") or a guess ("Dana?").
    [System.Text.RegularExpressions.GeneratedRegex(@"^Speaker [A-Z]{1,2}$|\?$")]
    private static partial System.Text.RegularExpressions.Regex Unnamed();

    /// <summary>Consecutive segments by the same speaker, as <see cref="TranscriptFormat.MergeTurns"/> groups them, kept apart for the player.</summary>
    static List<List<LabelledSegment>> Turns(IReadOnlyList<LabelledSegment> segments)
    {
        var turns = new List<List<LabelledSegment>>();
        foreach (var s in segments)
        {
            if (string.IsNullOrWhiteSpace(s.Text)) continue;
            if (turns.Count > 0 && turns[^1][0].Speaker == s.Speaker) turns[^1].Add(s);
            else turns.Add([s]);
        }
        return turns;
    }

    static string Seconds(double s) => Math.Max(0, s).ToString("0.##", CultureInfo.InvariantCulture);

    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>The CSP source that allows exactly this inline script.</summary>
    public static string ScriptHash(string script) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

    /// <summary>
    /// Plays the transcript: a click on a line (or its time) plays from just before it; the line being said is
    /// lit, with any earlier one still going (crosstalk); the page scrolls along only while the lit line was in
    /// view, so reading elsewhere isn't interrupted. A moment asked for — #t=754 in the page's address (a
    /// lemur://note/ link) or a summary item's time — lights the line said then, brings it into view and offers
    /// "▶ Play from 12:34", since browsers start sound only on a click. Reads nothing from the transcript but its
    /// data-t/data-e numbers. Line endings are LF because browsers normalise them before hashing.
    /// </summary>
    public static readonly string PlayerScript = """

        (() => {
          const audio = document.getElementById("audio");
          const list = document.querySelector(".transcript");
          const lines = [...list.querySelectorAll("span[data-t]")]
            .map(el => ({ el, t: +el.dataset.t, e: +el.dataset.e }))
            .sort((a, b) => a.t - b.t);
          const lead = 0.5; // seconds before a line starts, so its first word isn't cut
          let gone = false;

          // The last line to start at or before t; -1 if none does.
          function last(t) {
            let i = -1;
            for (let lo = 0, hi = lines.length - 1; lo <= hi;) {
              const mid = (lo + hi) >> 1;
              if (lines[mid].t <= t) { i = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return i;
          }

          function play(t) {
            if (gone) return;
            unmark();
            audio.currentTime = Math.max(0, t - lead);
            audio.play();
          }

          list.addEventListener("click", ev => {
            const link = ev.target.closest("time a");
            const line = link ? link.closest("li").querySelector("span[data-t]") : ev.target.closest("span[data-t]");
            if (!line) return;
            if (link) ev.preventDefault();
            else if (!document.getSelection().isCollapsed) return; // selecting text, not asking to listen
            play(+line.dataset.t);
          });

          const onScreen = el => {
            const r = el.getBoundingClientRect();
            return r.bottom > 0 && r.top < innerHeight;
          };
          let lit = [];
          function update() {
            const t = audio.currentTime;
            const i = last(t);
            const now = [];
            for (let j = i; j >= 0 && j > i - 50; j--) if (j === i || lines[j].e > t) now.push(lines[j].el);
            if (now.length === lit.length && now.every(el => lit.includes(el))) return;
            const following = lit.some(onScreen);
            lit.forEach(el => el.classList.remove("now"));
            now.forEach(el => el.classList.add("now"));
            if (now.length && following && !onScreen(now[0])) now[0].scrollIntoView({ block: "center", behavior: "smooth" });
            lit = now;
          }
          audio.addEventListener("timeupdate", update);
          audio.addEventListener("seeked", update);

          audio.addEventListener("error", () => {
            gone = true;
            list.classList.remove("playable");
            const player = document.getElementById("player");
            player.textContent = "The recording (" + decodeURIComponent(audio.getAttribute("src")) + ") isn't beside this page any more.";
            player.classList.add("gone");
          });

          const from = document.createElement("button");
          from.type = "button";
          from.className = "from";
          from.hidden = true;
          document.querySelector("#player .hint").prepend(from);
          const two = n => String(n).padStart(2, "0");
          const clock = s => (s >= 3600 ? (s / 3600 | 0) + ":" + two((s / 60 | 0) % 60) : (s / 60 | 0)) + ":" + two(s % 60);
          let start = 0, marked = null;
          function unmark() {
            if (marked) marked.classList.remove("at");
            marked = null;
          }
          function show(at) {
            unmark();
            from.hidden = at === null;
            if (at === null) return;
            // Whole seconds, rounded down, against lines to the hundredth: a line starting within that second counts.
            const line = lines[Math.max(0, last(at + 0.99))];
            start = line ? Math.min(at, line.t) : at;
            // Said after the line ended (a silence, past the end): it plays the line, so it says the line's time.
            from.textContent = "\u25B6\uFE0E Play from " + clock(line && at > line.e ? Math.floor(start) : at);
            if (!line) return;
            marked = line.el;
            marked.classList.add("at");
            marked.scrollIntoView({ block: "center" }); // a jump, as to any #section
          }
          from.addEventListener("click", () => play(start));
          audio.addEventListener("play", unmark); // the line being said is lit from here on

          const asked = () => {
            const m = /^#t=([0-9]{1,6})$/.exec(location.hash);
            return m ? +m[1] : null;
          };
          addEventListener("hashchange", () => show(asked()));
          show(asked());

          document.querySelector(".summary").addEventListener("click", ev => {
            const link = ev.target.closest("a.moment");
            if (!link) return;
            ev.preventDefault();
            show(+link.dataset.t);
            play(start);
          });
        })();

        """.ReplaceLineEndings("\n");

    const string Head = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="Content-Security-Policy" content="{csp}">
        <meta name="color-scheme" content="light dark">
        <meta name="generator" content="{generator}">
        <title>{title}</title>
        <style>
        :root {
          --bg: #ffffff; --fg: #1f2328; --muted: #656d76; --line: #d8dee4; --card: #f3f6f9;
          --me: #0b62c4; --s0: #8250df; --s1: #bf3989; --s2: #1a7f37; --s3: #bc4c00; --s4: #0e7c86; --s5: #9a6700;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0d1117; --fg: #e6edf3; --muted: #8d96a0; --line: #30363d; --card: #161b22;
            --me: #4493f8; --s0: #ab7df8; --s1: #db61a2; --s2: #3fb950; --s3: #db6d28; --s4: #39c5cf; --s5: #d29922;
          }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 "Segoe UI", system-ui, sans-serif; overflow-wrap: break-word; }
        main { max-width: 52rem; margin: 0 auto; padding: 2rem 1rem 4rem; }
        h1 { font-size: 1.75rem; line-height: 1.25; margin: 0 0 .25rem; }
        h2 { font-size: 1.2rem; margin: 2rem 0 .5rem; padding-bottom: .3rem; border-bottom: 1px solid var(--line); }
        .meta { color: var(--muted); margin: 0; }
        .hint { font-size: .85em; }
        a { color: var(--me); }
        code { font: .9em Consolas, ui-monospace, monospace; background: var(--card); padding: .1em .3em; border-radius: 4px; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid var(--line); padding: .3rem .6rem; text-align: left; vertical-align: top; }
        ul.contains-task-list { padding-left: 1.5rem; }
        li.task-list-item { list-style: none; }
        li.task-list-item input { margin: 0 .5rem 0 -1.5rem; }
        .transcript { list-style: none; padding: 0; margin: 0; }
        .transcript li {
          display: grid; grid-template-columns: 5rem 1fr; column-gap: .75rem;
          margin: .25rem 0; padding: .45rem .75rem; border-left: 3px solid var(--c); border-radius: 0 6px 6px 0;
        }
        .transcript time { grid-row: span 2; color: var(--muted); font: .85em/1.9 Consolas, ui-monospace, monospace; }
        .transcript b { color: var(--c); }
        .transcript p { margin: 0; }
        .transcript .me { --c: var(--me); background: var(--card); }
        .player { position: sticky; top: 0; z-index: 1; background: var(--bg); margin-top: 1rem; padding: .6rem 0 .4rem; border-bottom: 1px solid var(--line); }
        .player audio { display: block; width: 100%; }
        .player .hint { color: var(--muted); margin: .2rem 0 0; }
        .player.gone { position: static; color: var(--muted); }
        .transcript time a { color: inherit; text-decoration: none; }
        .playable time a:hover { color: var(--fg); text-decoration: underline; }
        .playable span[data-t] { cursor: pointer; border-radius: 3px; }
        .playable span[data-t]:hover { background: color-mix(in srgb, var(--c) 12%, transparent); }
        .playable span.now, .transcript span.at { background: color-mix(in srgb, var(--c) 26%, transparent); }
        .player button.from { font: inherit; color: var(--bg); background: var(--me); border: 0; border-radius: 4px; padding: .1rem .6rem; margin-right: .6rem; cursor: pointer; }
        .summary a.moment { font: .85em Consolas, ui-monospace, monospace; color: var(--muted); text-decoration: none; white-space: nowrap; }
        .summary a.moment::before { content: "\25B6\FE0E\00A0"; }
        .summary a.moment:hover { color: var(--fg); text-decoration: underline; }
        .s0 { --c: var(--s0); } .s1 { --c: var(--s1); } .s2 { --c: var(--s2); }
        .s3 { --c: var(--s3); } .s4 { --c: var(--s4); } .s5 { --c: var(--s5); }
        .attendees { margin: 1rem 0 0; }
        .attendees h2 { font-size: .78rem; font-weight: 600; letter-spacing: .06em; text-transform: uppercase; color: var(--muted); margin: 0 0 .45rem; padding: 0; border: 0; }
        .attendees h2 .n { font-weight: 400; }
        .attendees ul { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: .4rem; }
        .attendees li { display: inline-flex; align-items: baseline; gap: .45rem; padding: .2rem .65rem .2rem .55rem; border-left: 3px solid var(--c); border-radius: 0 6px 6px 0; background: var(--card); }
        .attendees li b { color: var(--c); font-weight: 600; }
        .attendees li span { color: var(--muted); font-size: .85em; }
        .attendees li.unnamed b { color: var(--muted); font-weight: 500; }
        .attendees .me { --c: var(--me); }
        .attendees .hint { margin-top: .5rem; }
        @media (max-width: 36rem) {
          .transcript li { grid-template-columns: 1fr; }
          .transcript time { grid-row: auto; line-height: 1.4; }
        }
        @media print {
          body { font-size: 11pt; }
          main { max-width: none; padding: 0; }
          .transcript li { break-inside: avoid; }
          .player { display: none; }
        }
        </style>
        </head>
        <body>
        <main>

        """;
}
