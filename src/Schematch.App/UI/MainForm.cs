using MaterialSkin;
using MaterialSkin.Controls;
using Schematch.App.Services;
using Schematch.Core.Compare;
using Schematch.Core.Model;
using Schematch.Core.Providers;
using Schematch.Core.Scripting;

namespace Schematch.App.UI;

public sealed class MainForm : MaterialForm
{
    private readonly AppSettings _settings = AppSettings.Load();

    private ConnectionInfo? _sourceInfo;
    private ConnectionInfo? _targetInfo;
    private SchemaComparisonResult? _comparison;
    private IDatabaseProvider? _provider;
    private CancellationTokenSource? _cts;
    private bool _busy;

    // Last schema read for each side, tagged with the connection it came from: refreshing one side
    // reuses the other side's snapshot, but only while that connection is still the one in use.
    private (ConnectionInfo Info, DatabaseSchema Schema)? _sourceSnapshot;
    private (ConnectionInfo Info, DatabaseSchema Schema)? _targetSnapshot;

    private readonly MaterialButton _sourceButton = new()
    {
        Text = "Source: (not set)",
        Type = MaterialButton.MaterialButtonType.Outlined,
        CharacterCasing = MaterialButton.CharacterCasingEnum.Normal,
        AutoSize = true,
        Margin = new Padding(4, 6, 2, 6),
    };
    private readonly MaterialButton _targetButton = new()
    {
        Text = "Target: (not set)",
        Type = MaterialButton.MaterialButtonType.Outlined,
        CharacterCasing = MaterialButton.CharacterCasingEnum.Normal,
        AutoSize = true,
        Margin = new Padding(2, 6, 4, 6),
    };
    private readonly MaterialButton _refreshSource = CreateRefreshButton("Refresh source");
    private readonly MaterialButton _refreshTarget = CreateRefreshButton("Refresh target");
    private readonly MaterialButton _swap = new()
    {
        Text = "⇄",
        Type = MaterialButton.MaterialButtonType.Outlined,
        AutoSize = false,
        Size = new Size(48, 36),
        Margin = new Padding(2, 6, 2, 6),
    };
    private readonly MaterialButton _compare = new()
    {
        Text = "Compare",
        Enabled = false,
        AutoSize = true,
        Margin = new Padding(12, 6, 4, 6),
    };
    private readonly MaterialButton _cancel = new()
    {
        Text = "Cancel",
        Type = MaterialButton.MaterialButtonType.Outlined,
        Enabled = false,
        AutoSize = true,
        Margin = new Padding(4, 6, 12, 6),
    };
    private readonly MaterialCheckbox _ignoreWhitespace = new()
    {
        Text = "Ignore whitespace in code objects",
        AutoSize = true,
        Margin = new Padding(4, 6, 0, 6),
    };
    private readonly MaterialCheckbox _includeDrops = new()
    {
        Text = "Script DROPs for target-only objects",
        AutoSize = true,
        Margin = new Padding(4, 6, 0, 6),
    };
    private readonly MaterialCheckbox _hideEqual = new()
    {
        Text = "Hide equal objects",
        AutoSize = true,
        Margin = new Padding(4, 6, 0, 6),
    };
    private readonly MaterialLabel _status = new()
    {
        AutoSize = true,
        FontType = MaterialSkinManager.fontType.Body2,
        Margin = new Padding(12, 14, 0, 0),
    };

    private readonly ListView _results = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = true,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        Font = new Font("Segoe UI", 9.5f),
    };
    // AutoCheck off so we drive the state ourselves; ThreeState shows the mixed (indeterminate) case.
    private readonly MaterialCheckbox _selectAll = new()
    {
        Text = "Select all",
        AutoSize = true,
        ThreeState = true,
        AutoCheck = false,
        Margin = new Padding(4, 2, 0, 0),
    };
    private bool _suppressCheckSync;
    private readonly ToolTip _connectionTips = new();
    private readonly DiffViewer _diff = new() { Dock = DockStyle.Fill };
    private readonly MaterialButton _generate = new()
    {
        Text = "Generate Deployment Script",
        Enabled = false,
        AutoSize = true,
        Margin = new Padding(4, 6, 4, 6),
    };
    private readonly MaterialButton _dataCompare = new()
    {
        Text = "Data Compare…",
        Type = MaterialButton.MaterialButtonType.Outlined,
        Enabled = false,
        AutoSize = true,
        Margin = new Padding(4, 6, 4, 6),
    };

    public MainForm()
    {
        Text = "Schematch — Database Compare";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 760);
        MinimumSize = new Size(900, 600);
        WindowState = FormWindowState.Maximized;
        Font = new Font("Segoe UI", 9f);
        MaterialTheme.Apply(this);

        _ignoreWhitespace.Checked = _settings.IgnoreWhitespaceInModules;
        _includeDrops.Checked = _settings.IncludeDrops;
        _hideEqual.Checked = _settings.HideEqualObjects;

        BuildLayout();

        _connectionTips.SetToolTip(_refreshSource, "Reload the source database and re-compare");
        _connectionTips.SetToolTip(_refreshTarget, "Reload the target database and re-compare");

        _sourceButton.Click += (_, _) => EditConnection(isSource: true);
        _targetButton.Click += (_, _) => EditConnection(isSource: false);
        _refreshSource.Click += async (_, _) => await RefreshAsync(source: true);
        _refreshTarget.Click += async (_, _) => await RefreshAsync(source: false);
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
        Disposed += (_, _) => _connectionTips.Dispose();
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

    /// <summary>Icon-only button that sits right after a connection button; disabled until there is a comparison to refresh.</summary>
    private static MaterialButton CreateRefreshButton(string accessibleName) => new()
    {
        Text = "↻",
        AccessibleName = accessibleName,
        Type = MaterialButton.MaterialButtonType.Text,
        Enabled = false,
        AutoSize = false,
        Size = new Size(48, 36),
        Margin = new Padding(0, 6, 4, 6),
    };

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 6, 8, 2),
            WrapContents = true,
        };
        top.Controls.AddRange(new Control[]
        {
            _sourceButton, _refreshSource, _swap, _targetButton, _refreshTarget, _compare, _cancel,
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
            BackColor = Color.FromArgb(224, 224, 224),
        };
        // Splitter positions are absolute, so set once the container has its real (docked) size.
        Load += (_, _) => split.SplitterDistance = Math.Max(330, split.Height * 45 / 100);
        split.Panel1.BackColor = Color.White;
        split.Panel2.BackColor = Color.White;
        var resultsHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(4, 2, 4, 2),
            BackColor = Color.White,
        };
        resultsHeader.Controls.Add(_selectAll);
        // Fill control added first so it fills the space left by the docked header.
        split.Panel1.Controls.Add(_results);
        split.Panel1.Controls.Add(resultsHeader);
        split.Panel2.Controls.Add(_diff);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
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
        UpdateConnectionButtons();
    }

    private void UpdateConnectionButtons()
    {
        // Buttons show the alias when set; the tooltip always says where the connection points.
        _sourceButton.Text = "Source: " + (_sourceInfo?.ShortDisplayName ?? "(not set)");
        _targetButton.Text = "Target: " + (_targetInfo?.ShortDisplayName ?? "(not set)");
        _connectionTips.SetToolTip(_sourceButton, _sourceInfo?.DisplayName ?? "Set the source database");
        _connectionTips.SetToolTip(_targetButton, _targetInfo?.DisplayName ?? "Set the target database");
        UpdateCommandStates();
    }

    /// <summary>Enables the toolbar for the current state: nothing that reads or acts on results while a load runs.</summary>
    private void UpdateCommandStates()
    {
        bool ready = !_busy && _sourceInfo is not null && _targetInfo is not null;
        _compare.Enabled = ready;
        // A refresh re-compares the results on screen, so there has to be a comparison to refresh.
        _refreshSource.Enabled = _refreshTarget.Enabled = ready && _comparison is not null;
        _sourceButton.Enabled = _targetButton.Enabled = _swap.Enabled = !_busy;
        _cancel.Enabled = _busy;
        _generate.Enabled = !_busy && _comparison is not null && _comparison.Differences.Any(d => d.Status != DiffStatus.Equal);
        _dataCompare.Enabled = !_busy && _comparison is not null;
    }

    /// <summary>Reads both databases from scratch and compares them.</summary>
    private Task CompareAsync() => LoadAndCompareAsync(reloadSource: true, reloadTarget: true, keepView: false);

    /// <summary>Re-reads one side and re-compares against the other side's last snapshot, keeping the user's place in the list.</summary>
    private Task RefreshAsync(bool source) => LoadAndCompareAsync(reloadSource: source, reloadTarget: !source, keepView: true);

    private async Task LoadAndCompareAsync(bool reloadSource, bool reloadTarget, bool keepView)
    {
        if (_busy || _sourceInfo is null || _targetInfo is null) return;
        // Pin the connections for this run so the snapshots are tagged with what was actually read.
        var sourceInfo = _sourceInfo;
        var targetInfo = _targetInfo;
        if (!sourceInfo.ProviderName.Equals(targetInfo.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Source and target must use the same database engine.",
                "Schematch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // A side the caller didn't ask for is still read if its snapshot is from another connection
        // (e.g. it was edited since the last compare) — reusing it would compare against the wrong database.
        var cachedSource = _sourceSnapshot is { } s && ReferenceEquals(s.Info, sourceInfo) ? s.Schema : null;
        var cachedTarget = _targetSnapshot is { } t && ReferenceEquals(t.Info, targetInfo) ? t.Schema : null;
        reloadSource |= cachedSource is null;
        reloadTarget |= cachedTarget is null;

        var provider = ProviderRegistry.Get(sourceInfo.ProviderName);
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateCommandStates();
        var progress = new Progress<string>(msg => _status.Text = msg);

        try
        {
            var sourceTask = reloadSource ? provider.ReadSchemaAsync(sourceInfo, progress, _cts.Token) : Task.FromResult(cachedSource!);
            var targetTask = reloadTarget ? provider.ReadSchemaAsync(targetInfo, progress, _cts.Token) : Task.FromResult(cachedTarget!);
            await Task.WhenAll(sourceTask, targetTask);
            _sourceSnapshot = (sourceInfo, sourceTask.Result);
            _targetSnapshot = (targetInfo, targetTask.Result);

            _status.Text = "Comparing…";
            var options = new CompareOptions
            {
                IgnoreWhitespaceInModules = _ignoreWhitespace.Checked,
                IgnoreCase = _settings.IgnoreCase,
            };
            _comparison = new SchemaComparer(options).Compare(sourceTask.Result, targetTask.Result);
            _provider = provider;

            PopulateResults(keepView);
            _diff.SetHeaders($"Source — {sourceInfo.DisplayName}", $"Target — {targetInfo.DisplayName}");

            int changed = _comparison.Differences.Count(d => d.Status != DiffStatus.Equal);
            _status.Text = $"{_comparison.Differences.Count} object(s) compared, {changed} difference(s).";
            // Say what a refresh actually re-read, so a refresh that found no change still visibly happened.
            if (keepView)
            {
                string reloaded = reloadSource && reloadTarget ? "Source and target" : reloadSource ? "Source" : "Target";
                _status.Text += $" {reloaded} reloaded at {DateTime.Now:HH:mm:ss}.";
            }
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
            _busy = false;
            _cts = null;
            UpdateCommandStates();
        }
    }

    private static string ViewKey(ObjectDifference diff) => $"{diff.Type}|{diff.FullName}";

    /// <param name="keepView">
    /// Restore the selected row, and the check mark of every object whose status didn't change.
    /// An object whose status did change (e.g. now Equal after a deployment) gets the default instead.
    /// </param>
    private void PopulateResults(bool keepView = false)
    {
        var previousChecks = new Dictionary<string, (DiffStatus Status, bool Checked)>(StringComparer.Ordinal);
        string? previousSelection = null;
        if (keepView)
        {
            foreach (ListViewItem existing in _results.Items)
                if (existing.Tag is ObjectDifference d)
                    previousChecks[ViewKey(d)] = (d.Status, existing.Checked);
            if (_results.SelectedItems.Count > 0 && _results.SelectedItems[0].Tag is ObjectDifference selected)
                previousSelection = ViewKey(selected);
        }

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
                bool check = previousChecks.TryGetValue(ViewKey(diff), out var previous) && previous.Status == diff.Status
                    ? previous.Checked
                    : diff.Status != DiffStatus.Equal;
                var item = new ListViewItem(diff.FullName, group)
                {
                    Tag = diff,
                    Checked = check,
                    ForeColor = diff.Status switch
                    {
                        DiffStatus.SourceOnly => MaterialTheme.SourceOnlyColor,
                        DiffStatus.TargetOnly => MaterialTheme.TargetOnlyColor,
                        DiffStatus.Different => MaterialTheme.DifferentColor,
                        _ => MaterialTheme.EqualColor,
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

        if (previousSelection is null) return;
        // Selecting re-renders the diff pane, so it shows the freshly loaded definition.
        var reselect = _results.Items.Cast<ListViewItem>()
            .FirstOrDefault(i => i.Tag is ObjectDifference d && ViewKey(d) == previousSelection);
        if (reselect is null) return;
        reselect.Selected = true;
        reselect.Focused = true;
        reselect.EnsureVisible();
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
