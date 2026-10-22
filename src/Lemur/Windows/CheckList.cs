using Lemur.Backends;
using Lemur.Config;

namespace Lemur.App.Windows;

/// <summary>"Test connection" results: one green ✓ or red ✗ line per server.</summary>
sealed class CheckList : TableLayoutPanel
{
    public CheckList()
    {
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = Ui.Wide;
        Margin = new Padding(0, 0, 0, 6);
    }

    public void Running() => Set([("Testing… (this can take up to 45 seconds)", SystemColors.GrayText)]);

    public void Clear() => Set([]);

    public void ShowResults(IReadOnlyList<CheckResult> results) => Set(results.Count == 0
        ? [("Nothing to check.", SystemColors.GrayText)]
        : results.Select(r => ($"{(r.Ok ? "✓" : "✗")}  {r.What}: {r.Detail}", r.Ok ? Ui.Good : Ui.Bad)).ToList());

    void Set(IReadOnlyList<(string Text, Color Colour)> lines)
    {
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();
        RowStyles.Clear();
        RowCount = 0;
        foreach (var (text, colour) in lines)
        {
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(new Label { Text = text, AutoSize = true, Anchor = Ui.Wide, ForeColor = colour, Margin = new Padding(3, 2, 3, 2) }, 0, RowCount++);
        }
        ResumeLayout();
    }

    /// <summary>Runs the checks off the UI thread on a snapshot of the settings; never throws except on cancel.</summary>
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(AppSettings snapshot, ISecretStore secrets, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => ConnectionTest.RunAsync(snapshot, secrets, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return [new CheckResult("Test", false, $"couldn't run: {DiagnosticsReport.Redact(ex.Message)}")];
        }
    }
}
