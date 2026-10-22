using System.Globalization;

namespace Lemur.Pipeline;

/// <summary>
/// The summary instructions live in the client, not the server, so every backend produces the same
/// shape of note and the prompt can name the user. Override with settings.summary.promptFile;
/// "{name}" in the file is replaced with the display name and "{date}" with the meeting's day ("Wednesday 2026-10-14").
/// The reply's "Title:" and "About:" lines are taken off before the note is written (<see cref="MeetingAbout.Split"/>);
/// a prompt without them still works. Action items come back as `- [ ] owner - task - due [hh:mm:ss]`, which
/// <see cref="ActionItems"/> reads for the meeting file; the date lets the model turn "Friday" into a date.
/// </summary>
public static class SummaryPrompt
{
    public const string Default = """
        You are writing meeting notes for {name}, who recorded this meeting on {date} and wants to stop forgetting
        what was agreed. Lines in the transcript are labelled with who spoke: "{name}" is the person who
        recorded it; "Speaker A", "Speaker B", ... are other people, whose real names may come up in what is said.

        Begin with these two lines:
        Title: <3 to 8 words saying what the meeting was about; no date>
        About: <one sentence of at most 30 words: what the meeting was about and the most important thing that came out of it>

        Then write Markdown with exactly these sections:

        ## Summary
        3-5 sentences: why the meeting happened and where it landed.

        ## Action items
        A checklist, one line each: `- [ ] <owner> - <task> - <due date> [<hh:mm:ss>]`.
        The owner is the person who committed to it: "{name}" when {name} said they would do it, a real name
        if one was used for that person, otherwise their speaker label. Write the due date like "Fri 2026-10-16",
        counting from the meeting's date; if none was said, leave it out along with its " - ". The time is that of
        the transcript line where it was agreed. Include only things someone actually agreed to do, not ideas
        that were floated.

        ## Decisions
        Bullets of what was decided, each with the reason if one was given.

        ## Open questions
        Bullets of things raised but not resolved, and who is expected to follow up if stated.

        ## Concepts and terms
        Bullets of technical concepts, product names, acronyms or ideas worth remembering:
        `**term** - what it means as used in this meeting`.

        Rules: use only what is in the transcript. Never invent names, dates, numbers or commitments.
        If a section has nothing, write "None." Keep it under 500 words unless the meeting was long.
        """;

    /// <param name="startedAt">When the meeting began; its day (on this PC's clock, as the note shows it) is "{date}".</param>
    /// <param name="glossary">How the vocabulary's words are spelled (<see cref="Transcripts.Vocabulary.Glossary"/>), added at the end.</param>
    public static string For(string displayName, string? promptFile, DateTimeOffset startedAt, string? glossary = null)
    {
        var template = !string.IsNullOrWhiteSpace(promptFile) && File.Exists(promptFile)
            ? File.ReadAllText(promptFile)
            : Default;
        var prompt = template.Replace("{name}", displayName).Replace("{date}", Day(startedAt));
        return glossary is null ? prompt : prompt.TrimEnd() + "\n\n" + glossary;
    }

    /// <summary>"Wednesday 2026-10-14": in English like the prompt, so the model's "Fri 2026-10-16" reads back.</summary>
    static string Day(DateTimeOffset startedAt) => startedAt.ToLocalTime().ToString("dddd yyyy-MM-dd", CultureInfo.InvariantCulture);
}
