using System.Data.Common;
using Schematch.Core.Providers;

namespace Schematch.App.UI;

/// <summary>Shows a generated SQL script with copy/save, and optional guarded execution against the target.</summary>
public sealed class ScriptForm : Form
{
    private readonly TextBox _script = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9.5f),
        Dock = DockStyle.Fill,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        ReadOnly = true,
        Font = new Font("Consolas", 8.5f),
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(30, 30, 30),
        ForeColor = Color.Gainsboro,
    };

    private readonly IDatabaseProvider? _provider;
    private readonly ConnectionInfo? _target;
    private readonly Button _execute = new() { Text = "Execute against target…", AutoSize = true };

    public ScriptForm(string title, string script, IDatabaseProvider? provider = null, ConnectionInfo? target = null)
    {
        _provider = provider;
        _target = target;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(950, 650);
        Font = new Font("Segoe UI", 9f);
        _script.Text = script;

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 480,
            Panel2Collapsed = true,
        };
        split.Panel1.Controls.Add(_script);
        split.Panel2.Controls.Add(_log);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) => { if (_script.Text.Length > 0) Clipboard.SetText(_script.Text); };
        var save = new Button { Text = "Save…", AutoSize = true };
        save.Click += (_, _) => SaveToFile();
        _execute.Visible = provider is not null && target is not null;
        _execute.Click += async (_, _) => await ExecuteAsync(split);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { copy, save, _execute, close });

        Controls.Add(split);
        Controls.Add(buttons);
        CancelButton = close;
    }

    private void SaveToFile()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "SQL scripts (*.sql)|*.sql|All files (*.*)|*.*",
            FileName = "schematch-deploy.sql",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, _script.Text);
    }

    private async Task ExecuteAsync(SplitContainer split)
    {
        if (_provider is null || _target is null) return;

        string typed = Microsoft.VisualBasic.Interaction.InputBox(
            $"This will run the script against:\n\n    {_target.Host} · {_target.Database}\n\n" +
            $"Type the target database name ({_target.Database}) to confirm.",
            "Confirm execution", "");
        if (!typed.Equals(_target.Database, StringComparison.OrdinalIgnoreCase))
        {
            if (typed.Length > 0)
                MessageBox.Show(this, "Database name did not match — nothing was executed.", "Cancelled",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        split.Panel2Collapsed = false;
        _log.Clear();
        _execute.Enabled = false;
        Log($"Connecting to {_target.Host} · {_target.Database}…");

        try
        {
            await using var conn = _provider.CreateConnection(_target);
            await conn.OpenAsync();

            var batches = _provider.SplitBatches(_script.Text);
            Log($"{batches.Count} batch(es) to execute.");
            int n = 0;
            foreach (var batch in batches)
            {
                n++;
                Log($"-- batch {n}/{batches.Count} --");
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = 0;
                    int affected = await cmd.ExecuteNonQueryAsync();
                    Log(affected >= 0 ? $"OK ({affected} row(s) affected)" : "OK");
                }
                catch (DbException ex)
                {
                    Log($"ERROR: {ex.Message}");
                    Log("Attempting ROLLBACK…");
                    await TryRollbackAsync(conn);
                    Log("Execution stopped.");
                    return;
                }
            }
            Log("Script completed successfully.");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            _execute.Enabled = true;
        }
    }

    private static async Task TryRollbackAsync(DbConnection conn)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ROLLBACK";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // No open transaction (XACT_ABORT already rolled back) — fine.
        }
    }

    private void Log(string line)
    {
        _log.AppendText(line + Environment.NewLine);
    }
}
