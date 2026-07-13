using Schematch.App.Services;
using Schematch.Core.Compare;
using Schematch.Core.Model;
using Schematch.Core.Providers;
using Schematch.Core.Scripting;

namespace Schematch.App.UI;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();

    private ConnectionInfo? _sourceInfo;
    private ConnectionInfo? _targetInfo;
    private SchemaComparisonResult? _comparison;
    private IDatabaseProvider? _provider;
    private CancellationTokenSource? _cts;

    private readonly Button _sourceButton = new() { Text = "Source: (not set)", AutoSize = true, Padding = new Padding(4) };
    private readonly Button _targetButton = new() { Text = "Target: (not set)", AutoSize = true, Padding = new Padding(4) };
    private readonly Button _swap = new() { Text = "⇄", AutoSize = true, Padding = new Padding(2) };
    private readonly Button _compare = new() { Text = "Compare", AutoSize = true, Padding = new Padding(4), Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly CheckBox _ignoreWhitespace = new() { Text = "Ignore whitespace in code objects", AutoSize = true };
    private readonly CheckBox _includeDrops = new() { Text = "Script DROPs for target-only objects", AutoSize = true };
    private readonly CheckBox _hideEqual = new() { Text = "Hide equal objects", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(10, 8, 0, 0), ForeColor = Color.DimGray };

    private readonly ListView _results = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = true,
        HideSelection = false,
    };
    // AutoCheck off so we drive the state ourselves; ThreeState shows the mixed (indeterminate) case.
    private readonly CheckBox _selectAll = new() { Text = "Select all", AutoSize = true, ThreeState = true, AutoCheck = false, Margin = new Padding(4, 4, 0, 0) };
    private bool _suppressCheckSync;
    private readonly DiffViewer _diff = new() { Dock = DockStyle.Fill };
    private readonly Button _generate = new() { Text = "Generate Deployment Script", AutoSize = true, Enabled = false };
    private readonly Button _dataCompare = new() { Text = "Data Compare…", AutoSize = true, Enabled = false };

    public MainForm()
    {
        Text = "Schematch — Database Compare";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 760);
        MinimumSize = new Size(900, 600);
        WindowState = FormWindowState.Maximized;
        Font = new Font("Segoe UI", 9f);

        _ignoreWhitespace.Checked = _settings.IgnoreWhitespaceInModules;
        _includeDrops.Checked = _settings.IncludeDrops;
        _hideEqual.Checked = _settings.HideEqualObjects;

        BuildLayout();

        _sourceButton.Click += (_, _) => EditConnection(isSource: true);
        _targetButton.Click += (_, _) => EditConnection(isSource: false);
        _swap.Click += (_, _) => SwapConnections();
        _compare.Click += async (_, _) => await CompareAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _hideEqual.CheckedChanged += (_, _) => PopulateResults();
        _selectAll.Click += (_, _) => ToggleSelectAll();
        _results.ItemChecked += (_, _) => { if (!_suppressCheckSync) UpdateSelectAllState(); };
        _results.SelectedIndexChanged += (_, _) => ShowSelectedDiff();
        _generate.Click += (_, _) => GenerateScript();
        _dataCompare.Click += (_, _) => OpenDataCompare();
        FormClosing += (_, _) => SaveOptions();
    }

    /// <summary>
    /// Test/demo seam: preload two connections and run the compare once the window is shown.
    /// Used for screenshot/E2E verification without driving the connection dialogs by hand.
    /// </summary>
    public void LoadDemo(ConnectionInfo source, ConnectionInfo target)
    {
        _sourceInfo = source;
        _targetInfo = target;
        UpdateConnectionButtons();
        Shown += async (_, _) => await CompareAsync();
    }

    /// <summary>Test/demo seam: preload connections without comparing (e.g. to inspect the connection dialog).</summary>
    public void PreloadConnections(ConnectionInfo? source, ConnectionInfo? target)
    {
        if (source is not null) _sourceInfo = source;
        if (target is not null) _targetInfo = target;
        UpdateConnectionButtons();
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            WrapContents = true,
        };
        top.Controls.AddRange(new Control[]
        {
            _sourceButton, _swap, _targetButton, _compare, _cancel,
            _ignoreWhitespace, _includeDrops, _hideEqual, _status,
        });

        _results.Columns.Add("Object", 340);
        _results.Columns.Add("Type", 100);
        _results.Columns.Add("Status", 130);
        _results.Columns.Add("Differences", 540);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330,
        };
        var resultsHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        resultsHeader.Controls.Add(_selectAll);
        // Fill control added first so it fills the space left by the docked header.
        split.Panel1.Controls.Add(_results);
        split.Panel1.Controls.Add(resultsHeader);
        split.Panel2.Controls.Add(_diff);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        bottom.Controls.AddRange(new Control[] { _generate, _dataCompare });

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private void EditConnection(bool isSource)
    {
        var existing = isSource ? _sourceInfo : _targetInfo;
        using var dialog = new ConnectionDialog(_settings, existing, isSource ? "Source database" : "Target database");
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        _settings.RememberConnection(dialog.Result, dialog.SavePassword);
        if (isSource) _sourceInfo = dialog.Result;
        else _targetInfo = dialog.Result;
        UpdateConnectionButtons();
    }

    private void SwapConnections()
    {
        (_sourceInfo, _targetInfo) = (_targetInfo, _sourceInfo);
        _comparison = null;
        _results.Items.Clear();
        _diff.Clear();
        _generate.Enabled = _dataCompare.Enabled = false;
        UpdateConnectionButtons();
    }

    private void UpdateConnectionButtons()
    {
        _sourceButton.Text = "Source: " + (_sourceInfo?.DisplayName ?? "(not set)");
        _targetButton.Text = "Target: " + (_targetInfo?.DisplayName ?? "(not set)");
        _compare.Enabled = _sourceInfo is not null && _targetInfo is not null;
    }

    private async Task CompareAsync()
    {
        if (_sourceInfo is null || _targetInfo is null) return;
        if (!_sourceInfo.ProviderName.Equals(_targetInfo.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Source and target must use the same database engine.",
                "Schematch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _provider = ProviderRegistry.Get(_sourceInfo.ProviderName);
        _compare.Enabled = false;
        _cancel.Enabled = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => _status.Text = msg);

        try
        {
            var sourceTask = _provider.ReadSchemaAsync(_sourceInfo, progress, _cts.Token);
            var targetTask = _provider.ReadSchemaAsync(_targetInfo, progress, _cts.Token);
            await Task.WhenAll(sourceTask, targetTask);

            _status.Text = "Comparing…";
            var options = new CompareOptions
            {
                IgnoreWhitespaceInModules = _ignoreWhitespace.Checked,
                IgnoreCase = _settings.IgnoreCase,
            };
            _comparison = new SchemaComparer(options).Compare(sourceTask.Result, targetTask.Result);

            PopulateResults();
            _diff.SetHeaders($"Source — {_sourceInfo.DisplayName}", $"Target — {_targetInfo.DisplayName}");

            int changed = _comparison.Differences.Count(d => d.Status != DiffStatus.Equal);
            _status.Text = $"{_comparison.Differences.Count} object(s) compared, {changed} difference(s).";
            _generate.Enabled = changed > 0;
            _dataCompare.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = "Failed.";
            MessageBox.Show(this, ex.Message, "Comparison failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _compare.Enabled = true;
            _cancel.Enabled = false;
            _cts = null;
        }
    }

    private void PopulateResults()
    {
        _suppressCheckSync = true;
        _results.BeginUpdate();
        _results.Items.Clear();
        _results.Groups.Clear();
        _diff.Clear();

        if (_comparison is null)
        {
            _results.EndUpdate();
            _suppressCheckSync = false;
            UpdateSelectAllState();
            return;
        }

        foreach (var type in Enum.GetValues<SchemaObjectType>())
        {
            var group = new ListViewGroup(type + "s", HorizontalAlignment.Left);
            var diffs = _comparison.Differences
                .Where(d => d.Type == type && (!_hideEqual.Checked || d.Status != DiffStatus.Equal))
                .ToList();
            if (diffs.Count == 0) continue;
            _results.Groups.Add(group);

            foreach (var diff in diffs)
            {
                var item = new ListViewItem(diff.FullName, group)
                {
                    Tag = diff,
                    Checked = diff.Status != DiffStatus.Equal,
                    ForeColor = diff.Status switch
                    {
                        DiffStatus.SourceOnly => Color.FromArgb(0, 128, 0),
                        DiffStatus.TargetOnly => Color.FromArgb(178, 34, 34),
                        DiffStatus.Different => Color.FromArgb(200, 120, 0),
                        _ => Color.Gray,
                    },
                };
                item.SubItems.Add(diff.Type.ToString());
                item.SubItems.Add(diff.Status switch
                {
                    DiffStatus.SourceOnly => "Only in source",
                    DiffStatus.TargetOnly => "Only in target",
                    DiffStatus.Different => "Different",
                    _ => "Equal",
                });
                item.SubItems.Add(diff.Details.Count == 0 ? "" : string.Join("; ", diff.Details.Take(4))
                    + (diff.Details.Count > 4 ? $" … (+{diff.Details.Count - 4})" : ""));
                _results.Items.Add(item);
            }
        }
        _results.EndUpdate();
        _suppressCheckSync = false;
        UpdateSelectAllState();
    }

    /// <summary>Checks every visible row, or unchecks them all if they are already all checked.</summary>
    private void ToggleSelectAll()
    {
        if (_results.Items.Count == 0) return;
        bool check = _selectAll.CheckState != CheckState.Checked;
        _suppressCheckSync = true;
        _results.BeginUpdate();
        foreach (ListViewItem item in _results.Items)
            item.Checked = check;
        _results.EndUpdate();
        _suppressCheckSync = false;
        UpdateSelectAllState();
    }

    /// <summary>Reflects all / none / mixed selection in the header checkbox.</summary>
    private void UpdateSelectAllState()
    {
        int total = _results.Items.Count;
        int selected = _results.CheckedItems.Count;
        _selectAll.CheckState = total == 0 || selected == 0 ? CheckState.Unchecked
            : selected == total ? CheckState.Checked
            : CheckState.Indeterminate;
        _selectAll.Text = total == 0 ? "Select all" : $"Select all ({selected}/{total})";
    }

    private void ShowSelectedDiff()
    {
        if (_results.SelectedItems.Count == 0 || _results.SelectedItems[0].Tag is not ObjectDifference diff || _provider is null)
        {
            _diff.Clear();
            return;
        }
        _diff.ShowDiff(RenderDdl(diff, source: true), RenderDdl(diff, source: false));
    }

    private string RenderDdl(ObjectDifference diff, bool source)
    {
        if (diff.Type == SchemaObjectType.Table)
        {
            var table = source ? diff.SourceTable : diff.TargetTable;
            return table is null ? "" : RenderTableDdl(table);
        }
        var code = source ? diff.SourceCode : diff.TargetCode;
        return code?.Definition ?? "";
    }

    private string RenderTableDdl(TableModel table)
    {
        var sb = new System.Text.StringBuilder(_provider!.ScriptGenerator.ScriptCreateTable(table));
        foreach (var fk in table.ForeignKeys)
            sb.AppendLine($"-- FK {fk.Name}: ({string.Join(", ", fk.Columns)}) → {fk.ReferencedSchema}.{fk.ReferencedTable} ({string.Join(", ", fk.ReferencedColumns)})");
        foreach (var ix in table.Indexes)
            sb.AppendLine(ix.RawDefinition is not null
                ? $"-- {ix.RawDefinition}"
                : $"-- INDEX {ix.Name}{(ix.IsUnique ? " UNIQUE" : "")}: ({string.Join(", ", ix.Columns)})");
        return sb.ToString();
    }

    private void GenerateScript()
    {
        if (_comparison is null || _provider is null) return;

        var selected = _results.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag)
            .OfType<ObjectDifference>()
            .Where(d => d.Status != DiffStatus.Equal)
            .ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "No differences are checked.", "Schematch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = new ScriptOptions { IncludeDrops = _includeDrops.Checked };
        string script = _provider.ScriptGenerator.Generate(_comparison, selected, options);
        using var form = new ScriptForm("Deployment script (target ← source)", script, _provider, _targetInfo);
        form.ShowDialog(this);
    }

    private void OpenDataCompare()
    {
        if (_comparison is null || _provider is null || _sourceInfo is null || _targetInfo is null) return;
        using var form = new DataCompareForm(_provider, _sourceInfo, _targetInfo, _comparison);
        form.ShowDialog(this);
    }

    private void SaveOptions()
    {
        _settings.IgnoreWhitespaceInModules = _ignoreWhitespace.Checked;
        _settings.IncludeDrops = _includeDrops.Checked;
        _settings.HideEqualObjects = _hideEqual.Checked;
        _settings.Save();
    }
}
