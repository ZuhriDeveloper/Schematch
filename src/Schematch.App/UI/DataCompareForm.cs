using System.Text;
using MaterialSkin;
using MaterialSkin.Controls;
using Schematch.Core.Compare;
using Schematch.Core.Data;
using Schematch.Core.Model;
using Schematch.Core.Providers;

namespace Schematch.App.UI;

/// <summary>Row-data comparison for tables that exist on both sides with a primary key.</summary>
public sealed class DataCompareForm : MaterialForm
{
    private readonly IDatabaseProvider _provider;
    private readonly ConnectionInfo _source;
    private readonly ConnectionInfo _target;

    private readonly CheckedListBox _tables = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        Font = new Font("Segoe UI", 9.5f),
        IntegralHeight = false,
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BorderStyle = BorderStyle.None,
        BackgroundColor = Color.White,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
        GridColor = Color.FromArgb(230, 230, 230),
        EnableHeadersVisualStyles = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
    };
    private readonly TextBox _preview = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9f),
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
    };
    private readonly MaterialButton _run = new() { Text = "Compare Data", AutoSize = true, Margin = new Padding(4, 6, 4, 6) };
    private readonly MaterialButton _script = new() { Text = "Generate DML Script", Type = MaterialButton.MaterialButtonType.Outlined, AutoSize = true, Enabled = false, Margin = new Padding(4, 6, 4, 6) };
    private readonly MaterialButton _cancel = new() { Text = "Cancel", Type = MaterialButton.MaterialButtonType.Outlined, AutoSize = true, Enabled = false, Margin = new Padding(4, 6, 4, 6) };
    private readonly MaterialCheckbox _includeDeletes = new() { Text = "Include DELETEs for extra target rows", AutoSize = true, Checked = true, Margin = new Padding(8, 6, 0, 6) };
    private readonly MaterialLabel _status = new() { AutoSize = true, FontType = MaterialSkinManager.fontType.Body2, Margin = new Padding(12, 14, 0, 0) };

    private readonly List<(TableModel Source, TableModel Target)> _eligible = new();
    private readonly List<TableDataDiff> _results = new();
    private CancellationTokenSource? _cts;

    public DataCompareForm(IDatabaseProvider provider, ConnectionInfo source, ConnectionInfo target,
        SchemaComparisonResult comparison)
    {
        _provider = provider;
        _source = source;
        _target = target;

        Text = $"Data Compare — {source.Database} → {target.Database}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1000, 700);
        Font = new Font("Segoe UI", 9f);
        MaterialTheme.Apply(this);

        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(97, 97, 97);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.White;
        _grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.5f);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 234, 246); // indigo 50
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.RowTemplate.Height = 28;

        foreach (var diff in comparison.Differences.Where(d =>
                     d.Type == SchemaObjectType.Table && d.SourceTable is not null && d.TargetTable is not null))
        {
            if (diff.SourceTable!.PrimaryKey is null) continue;
            _eligible.Add((diff.SourceTable, diff.TargetTable!));
            string note = diff.Status == DiffStatus.Different ? "  (schema differs!)" : "";
            _tables.Items.Add(diff.SourceTable.FullName + note, diff.Status == DiffStatus.Equal);
        }

        _grid.Columns.Add("Table", "Table");
        _grid.Columns.Add("Missing", "Missing in target");
        _grid.Columns.Add("Extra", "Extra in target");
        _grid.Columns.Add("Different", "Different");
        _grid.Columns.Add("Equal", "Equal");
        _grid.Columns.Add("Note", "Note");
        _grid.SelectionChanged += (_, _) => ShowPreview();

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Color.FromArgb(224, 224, 224),
        };
        right.Panel1.BackColor = Color.White;
        right.Panel2.BackColor = Color.White;
        right.Panel1.Controls.Add(_grid);
        right.Panel2.Controls.Add(_preview);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(224, 224, 224),
        };
        split.Panel1.BackColor = Color.White;
        split.Panel2.BackColor = Color.White;
        split.Panel1.Controls.Add(_tables);
        split.Panel2.Controls.Add(right);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        _run.Click += async (_, _) => await RunAsync();
        _script.Click += (_, _) => OpenScript();
        _cancel.Click += (_, _) => _cts?.Cancel();
        buttons.Controls.AddRange(new Control[] { _run, _cancel, _script, _includeDeletes, _status });

        Controls.Add(split);
        Controls.Add(buttons);

        // Splitter positions are absolute, so they only make sense once the containers have their real size.
        Load += (_, _) =>
        {
            split.SplitterDistance = 280;
            right.SplitterDistance = 260;
        };
    }

    private async Task RunAsync()
    {
        var selected = _tables.CheckedIndices.Cast<int>().Select(i => _eligible[i]).ToList();
        if (selected.Count == 0)
        {
            _status.Text = "Select at least one table.";
            return;
        }

        _results.Clear();
        _grid.Rows.Clear();
        _preview.Clear();
        _run.Enabled = false;
        _script.Enabled = false;
        _cancel.Enabled = true;
        _cts = new CancellationTokenSource();
        var options = new DataCompareOptions { IncludeDeletes = _includeDeletes.Checked };

        try
        {
            foreach (var (src, tgt) in selected)
            {
                _status.Text = $"Comparing {src.FullName}…";
                var diff = await DataComparer.CompareTableAsync(_provider, _source, _target, src, tgt, options, _cts.Token);
                _results.Add(diff);
                _grid.Rows.Add(diff.TableName,
                    diff.MissingInTarget, diff.ExtraInTarget, diff.DifferentRows, diff.EqualRows,
                    diff.Error ?? (diff.HasChanges ? "" : "in sync"));
            }
            _status.Text = "Done.";
            _script.Enabled = _results.Any(r => r.HasChanges);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
        }
        finally
        {
            _run.Enabled = true;
            _cancel.Enabled = false;
            _cts = null;
        }
    }

    private void ShowPreview()
    {
        if (_grid.SelectedRows.Count == 0) return;
        int index = _grid.SelectedRows[0].Index;
        if (index < 0 || index >= _results.Count) return;
        var diff = _results[index];
        _preview.Text = diff.Samples.Count == 0
            ? (diff.Error ?? "No changes.")
            : string.Join(Environment.NewLine, diff.Samples)
              + (diff.Samples.Count < diff.MissingInTarget + diff.ExtraInTarget + diff.DifferentRows
                  ? Environment.NewLine + $"… ({diff.Samples.Count} of many statements shown)"
                  : "");
    }

    private void OpenScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- Schematch data deployment script ({_provider.Name})");
        sb.AppendLine($"-- Source: {_source.Database}   Target: {_target.Database}");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(_provider.TransactionStartStatement);
        foreach (var diff in _results.Where(r => r.HasChanges && r.Script.Length > 0))
        {
            sb.AppendLine();
            sb.Append(diff.Script);
        }
        sb.AppendLine();
        sb.AppendLine(_provider.TransactionEndStatement);

        using var form = new ScriptForm("Data deployment script", sb.ToString(), _provider, _target);
        form.ShowDialog(this);
    }
}
