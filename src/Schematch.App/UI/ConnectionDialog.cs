using Schematch.App.Services;
using Schematch.Core.Providers;

namespace Schematch.App.UI;

/// <summary>Connection editor: engine, structured fields OR a raw connection string, test, recent connections.</summary>
public sealed class ConnectionDialog : Form
{
    private readonly AppSettings _settings;

    private readonly ComboBox _recent = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _useConnString = new() { Text = "Enter a connection string directly", AutoSize = true };

    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 5432, Dock = DockStyle.Left, Width = 90 };
    private readonly ComboBox _auth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _username = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly ComboBox _database = new() { Dock = DockStyle.Fill };
    private readonly Button _loadDbs = new() { Text = "Load", Width = 60 };

    private readonly TextBox _connString = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Top,
        Height = 72,
        Font = new Font("Consolas", 9f),
    };

    private readonly CheckBox _savePassword = new() { Text = "Remember password (encrypted for this Windows user)", AutoSize = true };
    private readonly Button _test = new() { Text = "Test Connection", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Text = "" };

    private readonly Label _portLabel = Label_("Port");
    private readonly Label _authLabel = Label_("Authentication");
    private readonly Label _userLabel = Label_("Username");
    private readonly Label _passLabel = Label_("Password");

    private TableLayoutPanel _structuredPanel = null!;
    private TableLayoutPanel _connStringPanel = null!;

    public ConnectionInfo? Result { get; private set; }
    public bool SavePassword => _savePassword.Checked;

    public ConnectionDialog(AppSettings settings, ConnectionInfo? existing, string title)
    {
        _settings = settings;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(470, 420);
        Font = new Font("Segoe UI", 9f);

        BuildLayout();

        _provider.Items.AddRange(ProviderRegistry.All.Select(p => (object)p.Name).ToArray());
        _auth.Items.AddRange(new object[] { "Windows Authentication", "SQL Server Authentication" });

        _recent.Items.Add("(recent connections)");
        foreach (var saved in settings.RecentConnections)
            _recent.Items.Add(saved);
        _recent.SelectedIndex = 0;
        _recent.DisplayMember = nameof(SavedConnection.DisplayName);
        _recent.SelectedIndexChanged += (_, _) =>
        {
            if (_recent.SelectedItem is SavedConnection saved)
                LoadFrom(saved.ToConnectionInfo(),
                    savePassword: saved.ProtectedPassword is not null || saved.ProtectedConnectionString is not null,
                    rawMode: saved.UsesRawConnectionString);
        };

        _provider.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        _auth.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        _useConnString.CheckedChanged += (_, _) => UpdateFieldVisibility();
        _loadDbs.Click += async (_, _) => await LoadDatabasesAsync();
        _test.Click += async (_, _) => await TestAsync();

        if (existing is not null)
            LoadFrom(existing, savePassword: false, rawMode: existing.UsesRawConnectionString);
        else
        {
            _provider.SelectedIndex = 0;
            _auth.SelectedIndex = 0;
        }
        UpdateFieldVisibility();
    }

    private void BuildLayout()
    {
        _structuredPanel = BuildStructuredPanel();
        _connStringPanel = BuildConnStringPanel();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        void AddRow(Control? label, Control field, bool spanFull = false)
        {
            if (label is not null) grid.Controls.Add(label, 0, row);
            grid.Controls.Add(field, spanFull || label is null ? 0 : 1, row);
            if (spanFull || label is null) grid.SetColumnSpan(field, 2);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row++;
        }

        AddRow(Label_("Recent"), _recent);
        AddRow(Label_("Engine"), _provider);
        AddRow(null, _useConnString, spanFull: true);
        AddRow(null, _structuredPanel, spanFull: true);
        AddRow(null, _connStringPanel, spanFull: true);
        AddRow(null, _savePassword, spanFull: true);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (!TryBuildResult(out var info)) { DialogResult = DialogResult.None; return; }
            Result = info;
        };
        buttons.Controls.AddRange(new Control[] { _test, ok, cancel, _status });
        AddRow(null, buttons, spanFull: true);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
    }

    private TableLayoutPanel BuildStructuredPanel()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        void Add(Control label, Control field, Control? extra = null)
        {
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(field, 1, row);
            if (extra is not null) grid.Controls.Add(extra, 2, row);
            else grid.SetColumnSpan(field, 2);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row++;
        }

        Add(Label_("Server / Host"), _host);
        Add(_portLabel, _port);
        Add(_authLabel, _auth);
        Add(_userLabel, _username);
        Add(_passLabel, _password);
        Add(Label_("Database"), _database, _loadDbs);
        return grid;
    }

    private TableLayoutPanel BuildConnStringPanel()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(Label_("Connection string"), 0, 0);
        grid.Controls.Add(_connString, 0, 1);
        var hint = new Label
        {
            Text = "Engine (above) selects the driver. Include the database/catalog in the string.",
            AutoSize = true,
            ForeColor = Color.DimGray,
        };
        grid.Controls.Add(hint, 0, 2);
        return grid;
    }

    private static Label Label_(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Padding = new Padding(0, 5, 0, 0),
    };

    private void UpdateFieldVisibility()
    {
        bool raw = _useConnString.Checked;
        _structuredPanel.Visible = !raw;
        _connStringPanel.Visible = raw;

        bool isSqlServer = (string?)_provider.SelectedItem == "SQL Server";
        _portLabel.Visible = _port.Visible = !isSqlServer;
        _authLabel.Visible = _auth.Visible = isSqlServer;
        bool windowsAuth = isSqlServer && _auth.SelectedIndex == 0;
        _userLabel.Visible = _username.Visible = !windowsAuth;
        _passLabel.Visible = _password.Visible = !windowsAuth;

        _savePassword.Visible = raw || !windowsAuth;
        _savePassword.Text = raw
            ? "Remember connection string (encrypted for this Windows user)"
            : "Remember password (encrypted for this Windows user)";
    }

    private void LoadFrom(ConnectionInfo info, bool savePassword, bool rawMode)
    {
        _provider.SelectedItem = ProviderRegistry.Get(info.ProviderName).Name;
        _useConnString.Checked = rawMode;
        if (rawMode)
        {
            _connString.Text = info.ConnectionString ?? "";
        }
        else
        {
            _host.Text = info.Host;
            if (info.Port is int port) _port.Value = port;
            _auth.SelectedIndex = info.UseWindowsAuth ? 0 : 1;
            _username.Text = info.Username;
            _password.Text = info.Password;
            _database.Text = info.Database;
        }
        _savePassword.Checked = savePassword;
        UpdateFieldVisibility();
    }

    private bool TryBuildResult(out ConnectionInfo info)
    {
        string providerName = (string?)_provider.SelectedItem ?? "SQL Server";
        var provider = ProviderRegistry.Get(providerName);

        if (_useConnString.Checked)
        {
            string cs = _connString.Text.Trim();
            info = new ConnectionInfo { ProviderName = providerName, ConnectionString = cs };
            if (cs.Length == 0)
            {
                _status.Text = "Connection string is required.";
                return false;
            }
            try
            {
                provider.BuildConnectionString(info); // throws on malformed keywords
            }
            catch (Exception ex)
            {
                _status.Text = "Invalid connection string: " + FirstLine(ex.Message);
                return false;
            }
            info.Database = provider.ExtractDatabaseName(cs);
            if (info.Database.Trim().Length == 0)
            {
                _status.Text = "Connection string must name a database/catalog.";
                return false;
            }
            return true;
        }

        info = BuildStructuredInfo(providerName);
        if (info.Host.Trim().Length == 0)
        {
            _status.Text = "Server/host is required.";
            return false;
        }
        if (info.Database.Trim().Length == 0)
        {
            _status.Text = "Database is required.";
            return false;
        }
        return true;
    }

    /// <summary>Active-mode connection for Test/Load (no hard validation).</summary>
    private ConnectionInfo BuildInfo()
    {
        string providerName = (string?)_provider.SelectedItem ?? "SQL Server";
        if (_useConnString.Checked)
        {
            string cs = _connString.Text.Trim();
            var info = new ConnectionInfo { ProviderName = providerName, ConnectionString = cs };
            if (cs.Length > 0) info.Database = ProviderRegistry.Get(providerName).ExtractDatabaseName(cs);
            return info;
        }
        return BuildStructuredInfo(providerName);
    }

    private ConnectionInfo BuildStructuredInfo(string providerName)
    {
        bool isSqlServer = providerName == "SQL Server";
        return new ConnectionInfo
        {
            ProviderName = providerName,
            Host = _host.Text.Trim(),
            Port = isSqlServer ? null : (int)_port.Value,
            Database = _database.Text.Trim(),
            UseWindowsAuth = isSqlServer && _auth.SelectedIndex == 0,
            Username = _username.Text.Trim(),
            Password = _password.Text,
        };
    }

    private async Task LoadDatabasesAsync()
    {
        var info = BuildInfo();
        var provider = ProviderRegistry.Get(info.ProviderName);
        _status.Text = "Loading databases…";
        _loadDbs.Enabled = false;
        try
        {
            var databases = await provider.ListDatabasesAsync(info);
            _database.Items.Clear();
            _database.Items.AddRange(databases.Cast<object>().ToArray());
            if (_database.Items.Count > 0) _database.DroppedDown = true;
            _status.Text = $"{databases.Count} database(s).";
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + FirstLine(ex.Message);
        }
        finally
        {
            _loadDbs.Enabled = true;
        }
    }

    private async Task TestAsync()
    {
        var info = BuildInfo();
        var provider = ProviderRegistry.Get(info.ProviderName);
        _status.Text = "Connecting…";
        _test.Enabled = false;
        try
        {
            await using var conn = provider.CreateConnection(info);
            await conn.OpenAsync();
            _status.Text = "Connection OK.";
            _status.ForeColor = Color.Green;
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + FirstLine(ex.Message);
            _status.ForeColor = Color.Firebrick;
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        return nl < 0 ? s : s[..nl].TrimEnd();
    }
}
