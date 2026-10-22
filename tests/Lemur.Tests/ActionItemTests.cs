using System.Globalization;
using Lemur.Pipeline;

namespace Lemur.Tests;

/// <summary>The summary's action items read for the meeting file: owner, task, due date, time, and which are yours.</summary>
public class ActionItemTests
{
    /// <summary>A Wednesday.</summary>
    static readonly DateOnly Day = new(2026, 10, 14);

    static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US"), Uk = CultureInfo.GetCultureInfo("en-GB");

    static ActionItemList Read(string items, double seconds = 3600, string[]? others = null, CultureInfo? culture = null) =>
        ActionItems.Read($"## Summary\nWe met.\n\n## Action items\n{items}\n\n## Decisions\nNone.", Day, seconds, ["Paul"], others ?? ["Dana Whitfield"], culture ?? Us);

    [Fact]
    public void A_line_in_the_prompts_form_gives_owner_task_due_and_time()
    {
        var list = Read("- [ ] Paul - send Northwind the enterprise pricing - Fri 2026-10-16 [00:12:34]\n" +
                        "- [x] Dana Whitfield - rewrite the invite copy [00:21:50]");
        Assert.True(list.Found);
        Assert.Equal(
        [
            new ActionItem("send Northwind the enterprise pricing", "Paul", true, new DateOnly(2026, 10, 16), "Fri 2026-10-16", 754, false),
            new ActionItem("rewrite the invite copy", "Dana Whitfield", false, null, null, 1310, true),
        ], list.Items);
    }

    [Fact]
    public void Dashes_inside_the_task_stay_in_it()
    {
        var items = Read("- [ ] Paul - review the Q3 - Q4 deck - Friday\n- [ ] Paul - review the Q3 - Q4 deck\n" +
                         "- [ ] Paul – fix the build – end of next week").Items;
        Assert.Equal(("review the Q3 - Q4 deck", new DateOnly(2026, 10, 16)), (items[0].Text, items[0].Due));
        Assert.Equal(("review the Q3 - Q4 deck", null, null), (items[1].Text, items[1].Due, items[1].DueText)); // "Q4 deck" is no date
        Assert.Equal(("fix the build", new DateOnly(2026, 10, 23)), (items[2].Text, items[2].Due));            // en dashes too
    }

    [Fact]
    public void Vague_words_are_kept_but_pin_no_day_and_none_is_no_due_date()
    {
        var items = Read("- [ ] Paul - send it - next week\n- [ ] Paul - tag it - after the release\n- [ ] Paul - ship it - soon\n" +
                         "- [ ] Paul - file it - none\n- [ ] Paul - send the deck - by **Friday** at 3pm").Items;
        Assert.Equal([("send it", "next week"), ("tag it", "after the release"), ("ship it", "soon"), ("file it", null)],
            items.Take(4).Select(i => (i.Text, i.DueText)));
        Assert.All(items.Take(4), i => Assert.Null(i.Due));
        Assert.Equal(new DateOnly(2026, 10, 16), items[4].Due);
    }

    [Fact]
    public void A_line_without_a_dash_is_all_task_and_belongs_to_nobody()
    {
        var item = Assert.Single(Read("- [ ] Send the deck [00:01:00]").Items);
        Assert.Equal(new ActionItem("Send the deck", null, false, null, null, 60, false), item);
    }

    [Fact]
    public void A_task_with_only_a_date_after_it_belongs_to_nobody()
    {
        var list = Read("- [ ] Book the venue - Fri 2026-10-16 [00:01:00]");
        Assert.True(list.Found); // in the prompt's form, its owner left out
        Assert.Equal(new ActionItem("Book the venue", null, false, new DateOnly(2026, 10, 16), "Fri 2026-10-16", 60, false), Assert.Single(list.Items));
        // One " - " with no date after it is still owner and task.
        var owned = Assert.Single(Read("- [ ] Paul - send it").Items);
        Assert.Equal(("Paul", "send it"), (owned.Owner, owned.Text));
    }

    [Fact]
    public void Lines_indented_under_an_item_are_not_items()
    {
        var items = Read("- [ ] Paul - send the deck\n  - with the appendix\n\t* [ ] Dana Whitfield - check it\n    1. first\n" +
                         "- [ ] Dana Whitfield - book the room").Items;
        Assert.Equal(["send the deck", "book the room"], items.Select(i => i.Text));

        // A list that starts indented is still the list: only what is further in is a note on an item.
        var indented = Read("  - [ ] Paul - send the deck\n    - with the appendix\n  - [ ] Dana Whitfield - book the room").Items;
        Assert.Equal(["send the deck", "book the room"], indented.Select(i => i.Text));
    }

    [Theory]
    [InlineData("- [ ] Paul - send it - ２０２６-１０-２０")]
    [InlineData("- [ ] Paul - send it - ٢٠٢٦-١٠-٢٠")]
    [InlineData("- [ ] Paul - send it - Oct ２０")]
    [InlineData("- [ ] Paul - send it - the ２０th")]
    [InlineData("- [ ] Paul - send it - １０/２０")]
    [InlineData("- [ ] Paul - send it [１２:3４]")]
    public void Other_scripts_digits_are_no_date_or_time_and_never_a_crash(string line)
    {
        var item = Assert.Single(Read(line).Items);
        Assert.Equal(("Paul", (DateOnly?)null, (int?)null), (item.Owner, item.Due, item.At));
    }

    [Theory]
    [InlineData("send it [00:12:34]", 754, "00:12:34")]
    [InlineData("send it [12:34]", 754, "12:34")]
    [InlineData("send it (00:12:34)", 754, "00:12:34")]
    [InlineData("send it [00:12:34].", 754, "00:12:34")]
    [InlineData("send it [75:30]", 4530, "75:30")]
    [InlineData("send it [1:02:03] ", 3723, "1:02:03")]
    public void A_time_ending_a_line_is_read_however_the_model_wrote_it(string line, int seconds, string clock)
    {
        var time = ActionItems.TrailingTime(line);
        Assert.Equal((seconds, "send it ", clock), (time!.Seconds, line[..time.Index], time.Clock));
    }

    [Theory]
    [InlineData("send it")]
    [InlineData("send it [00:12:34] now")]
    [InlineData("send it [00:75:30]")]                           // with hours, minutes stop at 59
    [InlineData("send it [12:34:5]")]
    public void Anything_else_is_no_time(string line) => Assert.Null(ActionItems.TrailingTime(line));

    [Fact]
    public void A_time_past_the_end_is_dropped_and_older_summaries_have_none()
    {
        var late = Assert.Single(Read("- [ ] Paul - send it [01:02:03]", seconds: 600).Items);
        Assert.Equal(("send it", (int?)null), (late.Text, late.At));
        var old = Assert.Single(Read("- [ ] Paul - write it up").Items); // before the prompt asked for dates and times
        Assert.Equal(new ActionItem("write it up", "Paul", true, null, null, null, false), old);
    }

    [Fact]
    public void Without_the_section_or_its_form_owners_are_not_trusted()
    {
        var none = ActionItems.Read("## Summary\nWe met.\n\nNext: Paul writes it up.", Day, 60, ["Paul"], [], Us);
        Assert.False(none.Found);
        Assert.Empty(none.Items);

        var custom = ActionItems.Read("## Summary\nWe met.\n\n## Action Items:\n- Paul - write it up\nDana to send the numbers.", Day, 60, ["Paul"], [], Us);
        Assert.False(custom.Found);
        Assert.Equal(["Paul - write it up", "Dana to send the numbers."], custom.Items.Select(i => i.Text));
        Assert.All(custom.Items, i => Assert.Equal((null, false), (i.Owner, i.OwnerIsMe)));

        // "None." is the prompt's own empty section: found, with nothing in it.
        var empty = Read("None.");
        Assert.True(empty.Found);
        Assert.Empty(empty.Items);
    }

    [Theory]
    [InlineData("**Action items:**")]
    [InlineData("### ACTION ITEMS")]
    [InlineData("## Action items:")]
    public void The_heading_is_found_however_it_is_written(string heading)
    {
        var list = ActionItems.Read($"Some summary.\n\n{heading}\n- [ ] Paul - write it up\n\n## Decisions\n- x", Day, 60, ["Paul"], [], Us);
        Assert.True(list.Found);
        Assert.Equal("write it up", Assert.Single(list.Items).Text);
    }

    [Theory]
    [InlineData("Paul", "Paul", "", true)]
    [InlineData("  paul ", "Paul", "", true)]                    // case and spacing ignored
    [InlineData("Paul Smith", "Paul  Smith", "", true)]
    [InlineData("Paul", "Paul Smith", "", true)]                 // a first name for the full one, either way round
    [InlineData("Paul Smith", "Paul", "", true)]
    [InlineData("me", "Paul", "", true)]
    [InlineData("I", "Paul", "", true)]
    [InlineData("Paul's team", "Paul", "", false)]
    [InlineData("Paul and Dana", "Paul", "", false)]
    [InlineData("Pauline", "Paul", "", false)]
    [InlineData("Dana Whitfield", "Paul", "Dana Whitfield", false)]
    [InlineData("Paul", "Paul Smith", "Paul Jones", false)]      // a second Paul: a bare "Paul" is nobody's
    [InlineData("Paul Smith", "Paul", "Paul Jones", false)]
    [InlineData("Paul", "Paul Smith", "Paul?", false)]           // a guess at another Paul is still another Paul
    [InlineData("Paul", "Paul", "Paul", false)]                  // someone else is called exactly that
    [InlineData("Paul", "Paul", "Paul Jones", true)]             // the user's own label: the transcript told them apart
    public void Yours_means_your_name_and_nothing_looser(string owner, string me, string other, bool mine) =>
        Assert.Equal(mine, ActionItems.IsMe(owner, [me], other.Length == 0 ? [] : [other]));

    [Theory]
    [InlineData("Friday", "2026-10-16")]
    [InlineData("by Friday", "2026-10-16")]
    [InlineData("Fri", "2026-10-16")]
    [InlineData("Wednesday", "2026-10-21")]                      // said on a Wednesday: a week on
    [InlineData("this Wednesday", "2026-10-14")]
    [InlineData("next Friday", "2026-10-23")]                    // the one in the following week
    [InlineData("next Monday", "2026-10-19")]
    [InlineData("tomorrow", "2026-10-15")]
    [InlineData("today", "2026-10-14")]
    [InlineData("end of the week", "2026-10-16")]
    [InlineData("by end of week", "2026-10-16")]
    [InlineData("end of next week", "2026-10-23")]
    [InlineData("end of the month", "2026-10-31")]
    [InlineData("Oct 20", "2026-10-20")]
    [InlineData("October 20th", "2026-10-20")]
    [InlineData("20 October", "2026-10-20")]
    [InlineData("the 20th", "2026-10-20")]
    [InlineData("2026-10-20", "2026-10-20")]
    [InlineData("Fri 2026-10-16", "2026-10-16")]
    [InlineData("Friday, Oct 16", "2026-10-16")]
    [InlineData("Jan 5", "2027-01-05")]                          // no year: the first one on or after the meeting
    [InlineData("Thu 2026-10-16", null)]                         // the weekday and the date disagree
    [InlineData("next week", null)]
    [InlineData("soon", null)]
    [InlineData("Q4 deck", null)]
    public void Due_dates_count_from_the_meetings_day(string words, string? due) =>
        Assert.Equal(due is null ? null : DateOnly.Parse(due, CultureInfo.InvariantCulture), ActionItems.DueDate(words, Day, Us));

    [Fact]
    public void Numbers_are_read_in_the_users_date_order()
    {
        Assert.Equal(new DateOnly(2026, 10, 20), ActionItems.DueDate("10/20", Day, Us));
        Assert.Null(ActionItems.DueDate("10/20", Day, Uk));     // no 20th month
        Assert.Equal(new DateOnly(2026, 10, 20), ActionItems.DueDate("20/10", Day, Uk));
        Assert.Equal(new DateOnly(2026, 11, 12), ActionItems.DueDate("11/12", Day, Us));
        Assert.Equal(new DateOnly(2026, 12, 11), ActionItems.DueDate("11/12", Day, Uk));
        Assert.Equal(new DateOnly(2026, 12, 11), Assert.Single(Read("- [ ] Paul - send it - 11/12", culture: Uk).Items).Due);
    }

    [Fact]
    public void The_prompt_says_the_meetings_day_and_a_custom_one_can_too()
    {
        var start = new DateTimeOffset(new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Local));
        var prompt = SummaryPrompt.For("Paul", null, start);
        Assert.Contains("recorded this meeting on Wednesday 2026-10-14", prompt);
        Assert.Contains("`- [ ] <owner> - <task> - <due date> [<hh:mm:ss>]`", prompt);
        Assert.Contains("like \"Fri 2026-10-16\"", prompt);

        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "Notes for {name}, from {date}.");
            Assert.Equal("Notes for Paul, from Wednesday 2026-10-14.", SummaryPrompt.For("Paul", file, start));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Next_weekday_follows_the_cultures_week()
    {
        var sunday = new DateOnly(2026, 10, 18);
        Assert.Equal(new DateOnly(2026, 10, 30), ActionItems.DueDate("next Friday", sunday, Us)); // Sunday starts a US week
        Assert.Equal(new DateOnly(2026, 10, 23), ActionItems.DueDate("next Friday", sunday, Uk)); // and ends a British one
    }
}
