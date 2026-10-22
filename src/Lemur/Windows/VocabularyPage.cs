using Lemur.Config;
using Lemur.Transcripts;

namespace Lemur.App.Windows;

/// <summary>
/// Words and names the transcription gets wrong, and how to write them: a table of correct spellings and what each is
/// misheard as. Saving offers to fix newly added words in meetings already made (<see cref="SettingsForm"/>).
/// </summary>
sealed class VocabularyPage : SettingsPage
{
    readonly DataGridView _grid = new()
    {
        Height = 300, Anchor = Ui.Wide, Margin = new Padding(0, 6, 0, 6),
        AllowUserToAddRows = true, AllowUserToDeleteRows = true, AllowUserToResizeRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle, EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
    };

    public VocabularyPage(PageHost host) : base(host, "Vocabulary", "Words and names your transcription gets wrong, and how to write them.")
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Correct spelling", FillWeight = 35, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Misheard as (separate with commas)", FillWeight = 65, SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        Body.Add(new Label { Text = "Words and names", AutoSize = true, Font = Ui.HeadingFont, Margin = new Padding(0, 0, 0, 4) });
        Body.Add(Ui.Muted(
            "Your transcription doesn't know the names of your company's products, customers or people, so it writes what it " +
            "hears: \"Cooper Netties\" for Kubernetes. List each one as it should be written, and what it gets misheard as. Every new " +
            "meeting's transcript is fixed before its speakers are named and its summary is written, and your summary model " +
            "is told how the ones it has are spelled."));
        Body.Add(_grid);
        Body.Add(Ui.Line(Ui.Button("Remove", RemoveSelected)));
        Body.Add(Ui.Muted(
            "A word with nothing under \"Misheard as\" is only spelled out for the summary model: useful for acronyms and " +
            "unusual names. When you save, Lemur offers to fix the words you added in meetings already made. In the " +
            "Recordings window, Fix a word… does the same for one word."));
    }

    public override void LoadFrom(AppSettings s)
    {
        _grid.Rows.Clear();
        foreach (var e in s.Vocabulary) _grid.Rows.Add(e.Term, string.Join(", ", e.HeardAs));
    }

    public override void SaveTo(AppSettings s)
    {
        _grid.EndEdit();
        var list = new List<VocabularyEntry>();
        foreach (var (term, heard) in Rows())
        {
            if (term.Length == 0) continue; // Check says so
            if (heard.Count == 0) Vocabulary.Add(list, term);
            foreach (var h in heard) Vocabulary.Add(list, term, h);
        }
        s.Vocabulary = list;
    }

    public override void Check(List<string> errors)
    {
        _grid.EndEdit();
        foreach (var (term, heard) in Rows())
            if (term.Length == 0 && heard.Count > 0)
                errors.Add($"Vocabulary: \"{heard[0]}\" needs its correct spelling.");
        errors.AddRange(Vocabulary.Problems(Rows().Where(r => r.Term.Length > 0)
            .Select(r => new VocabularyEntry { Term = r.Term, HeardAs = [.. r.Heard] })).Select(p => "Vocabulary: " + p));
    }

    IEnumerable<(string Term, List<string> Heard)> Rows()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            var term = (row.Cells[0].Value as string ?? "").Trim();
            var heard = (row.Cells[1].Value as string ?? "")
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (term.Length > 0 || heard.Count > 0) yield return (term, heard);
        }
    }

    void RemoveSelected()
    {
        var rows = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).OfType<DataGridViewRow>()
            .Concat(_grid.SelectedRows.Cast<DataGridViewRow>()).Distinct().Where(r => !r.IsNewRow).ToList();
        foreach (var row in rows) _grid.Rows.Remove(row);
    }
}
