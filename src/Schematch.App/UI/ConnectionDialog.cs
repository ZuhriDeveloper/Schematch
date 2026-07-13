using Schematch.App.Services;
using Schematch.Core.Providers;

namespace Schematch.App.UI;

/// <summary>Connection editor: engine, host, auth, database picker, test, recent connections.</summary>
public sealed class ConnectionDialog : Form
{
    private readonly AppSettings _settings;

    private readonly ComboBox _recent = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 5432, Dock = DockStyle.Left, Width = 90 };
    private readonly ComboBox _auth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _username = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly ComboBox _database = new() { Dock = DockStyle.Fill };
    private readonly Button _loadDbs = new() { Text = "Load", Width = 60 };
    private readonly CheckBox _savePassword = new() { Text = "Remember password (encrypted for this Windows user)", AutoSize = true };
    private readonly Button _test = new() { Text = "Test Connection", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Text = "" };

    private readonly Label _portLabel = Label_("Port");
    private readonly Label _authLabel = Label_("Authentication");
    private readonly Label _userLabel = Label_("Username");
    private readonly Label _passLabel = Label_("Password");

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
        ClientSize = new Size(460, 360);
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
                LoadFrom(saved.ToConnectionInfo(), saved.ProtectedPassword is not null);
        };

        _provider.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        _auth.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        _loadDbs.Click += async (_, _) => await LoadDatabasesAsync();
        _test.Click += async (_, _) => await TestAsync();

        if (existing is not null)
            LoadFrom(existing, savePassword: false);
        else
        {
            _provider.SelectedIndex = 0;
            _auth.SelectedIndex = 0;
        }
        UpdateFieldVisibility();
    }

    private void BuildLayout()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 3,
            RowCount = 10,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        void Add(Control label, Control field, Control? extra = null)
        {
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(field, 1, row);
            if (extra is not null) grid.Controls.Add(extra, 2, row);
            else grid.SetColumnSpan(field, 2);
            row++;
        }

        Add(Label_("Recent"), _recent);
        Add(Label_("Engine"), _provider);
        Add(Label_("Server / Host"), _host);
        Add(_portLabel, _port);
        Add(_authLabel, _auth);
        Add(_userLabel, _username);
        Add(_passLabel, _password);
        Add(Label_("Database"), _database, _loadDbs);
        grid.Controls.Add(_savePassword, 1, row);
        grid.SetColumnSpan(_savePassword, 2);
        row++;

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, e) =>
        {
            if (!TryBuildResult(out var info))
            {
                DialogResult = DialogResult.None;
                return;
            }
            Result = info;
        };
        buttons.Controls.AddRange(new Control[] { _test, ok, cancel, _status });
        grid.Controls.Add(buttons, 0, row);
        grid.SetColumnSpan(buttons, 3);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
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
        bool isSqlServer = (string?)_provider.SelectedItem == "SQL Server";
        _portLabel.Visible = _port.Visible = !isSqlServer;
        _authLabel.Visible = _auth.Visible = isSqlServer;
        bool windowsAuth = isSqlServer && _auth.SelectedIndex == 0;
        _userLabel.Visible = _username.Visible = !windowsAuth;
        _passLabel.Visible = _password.Visible = !windowsAuth;
        _savePassword.Visible = !windowsAuth;
    }

    private void LoadFrom(ConnectionInfo info, bool savePassword)
    {
        _provider.SelectedItem = ProviderRegistry.Get(info.ProviderName).Name;
        _host.Text = info.Host;
        if (info.Port is int port) _port.Value = port;
        _auth.SelectedIndex = info.UseWindowsAuth ? 0 : 1;
        _username.Text = info.Username;
        _password.Text = info.Password;
        _database.Text = info.Database;
        _savePassword.Checked = savePassword;
        UpdateFieldVisibility();
    }

    private bool TryBuildResult(out ConnectionInfo info)
    {
        info = BuildInfo();
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

    private ConnectionInfo BuildInfo()
    {
        bool isSqlServer = (string?)_provider.SelectedItem == "SQL Server";
        return new ConnectionInfo
        {
            ProviderName = (string?)_provider.SelectedItem ?? "SQL Server",
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
