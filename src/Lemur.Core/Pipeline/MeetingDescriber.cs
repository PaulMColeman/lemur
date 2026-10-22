using System.Text.Json;
using Lemur.Speakers;

namespace Lemur.Pipeline;

/// <summary>
/// Asks the summary model for a meeting's title and one-sentence description, from its summary: for a summary that
/// came back without its "Title:" and "About:" lines, for notes made before Lemur wrote them, and for "Suggest a
/// title" when renaming. Reads only the summary, so it is quick even for a long meeting.
/// </summary>
public static class MeetingDescriber
{
    public const string Prompt = """
        You give a meeting a title and a one-sentence description, from its notes.

        Reply with JSON only, no other text:
        {"title":"<3 to 8 words saying what the meeting was about>","about":"<one sentence of at most 30 words: what the meeting was about and the most important thing that came out of it>"}

        Use only what the notes say. No date in the title, and not just "Meeting" or a person's name.
        """;

    /// <summary>How long a title may take before the notes are written without one.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    public static async Task<(string? Title, string? Description)> AskAsync(IChatModel chat, string summary, CancellationToken ct) =>
        Parse(await chat.AskAsync(Prompt, "Meeting notes:\n" + summary.Trim(), ct));

    /// <summary>Reads the model's reply, tolerating code fences and stray text around the JSON.</summary>
    public static (string? Title, string? Description) Parse(string reply)
    {
        int a = reply.IndexOf('{'), b = reply.LastIndexOf('}');
        if (a < 0 || b <= a) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(reply[a..(b + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? Str(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (MeetingAbout.ModelTitle(Str("title")), MeetingAbout.ModelDescription(Str("about") ?? Str("description")));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
