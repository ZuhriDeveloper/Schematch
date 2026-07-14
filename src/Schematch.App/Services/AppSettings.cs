using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Schematch.Core.Providers;

namespace Schematch.App.Services;

public sealed class SavedConnection
{
    public string ProviderName { get; set; } = "SQL Server";
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    public string Database { get; set; } = "";
    public string Schema { get; set; } = "";
    public bool UseWindowsAuth { get; set; } = true;
    public string Username { get; set; } = "";

    /// <summary>DPAPI-protected (current user), base64. Null when the user chose not to save it.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>True when this entry was created from a raw connection string rather than the structured fields.</summary>
    public bool UsesRawConnectionString { get; set; }

    /// <summary>DPAPI-protected raw connection string (it may embed a password, so the whole thing is a secret).</summary>
    public string? ProtectedConnectionString { get; set; }

    private string SchemaSuffix => string.IsNullOrEmpty(Schema) ? "" : $" [{Schema}]";

    public string DisplayName => UsesRawConnectionString
        ? $"{ProviderName}: {(string.IsNullOrEmpty(Database) ? "(connection string)" : Database + SchemaSuffix + " (connection string)")}"
        : $"{ProviderName}: {Host}{(Port is int p ? $":{p}" : "")} · {Database}{SchemaSuffix}" +
          (UseWindowsAuth ? "" : $" ({Username})");

    /// <summary>The recent-connections combo renders items via ToString (owner-drawn), so mirror DisplayName.</summary>
    public override string ToString() => DisplayName;

    public ConnectionInfo ToConnectionInfo() => new()
    {
        ProviderName = ProviderName,
        Host = Host,
        Port = Port,
        Database = Database,
        Schema = Schema,
        UseWindowsAuth = UseWindowsAuth,
        Username = Username,
        Password = Unprotect(ProtectedPassword),
        ConnectionString = UsesRawConnectionString ? Unprotect(ProtectedConnectionString) : null,
    };

    public static SavedConnection From(ConnectionInfo info, bool savePassword) => new()
    {
        ProviderName = info.ProviderName,
        Host = info.Host,
        Port = info.Port,
        Database = info.Database,
        Schema = info.Schema,
        UseWindowsAuth = info.UseWindowsAuth,
        Username = info.Username,
        UsesRawConnectionString = info.UsesRawConnectionString,
        ProtectedPassword = savePassword && info.Password.Length > 0 ? Protect(info.Password) : null,
        ProtectedConnectionString = info.UsesRawConnectionString && savePassword && info.ConnectionString!.Length > 0
            ? Protect(info.ConnectionString!) : null,
    };

    private static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return ""; // protected on another machine/user — just ask again
        }
    }
}

public sealed class AppSettings
{
    public List<SavedConnection> RecentConnections { get; set; } = new();
    public bool IgnoreWhitespaceInModules { get; set; } = true;
    public bool IgnoreCase { get; set; } = true;
    public bool IncludeDrops { get; set; }
    public bool HideEqualObjects { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Schematch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings must never block startup.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void RememberConnection(ConnectionInfo info, bool savePassword)
    {
        RecentConnections.RemoveAll(c =>
            c.ProviderName.Equals(info.ProviderName, StringComparison.OrdinalIgnoreCase) &&
            c.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase) &&
            c.Database.Equals(info.Database, StringComparison.OrdinalIgnoreCase) &&
            c.Schema.Equals(info.Schema, StringComparison.OrdinalIgnoreCase) &&
            c.Username.Equals(info.Username, StringComparison.OrdinalIgnoreCase));
        RecentConnections.Insert(0, SavedConnection.From(info, savePassword));
        if (RecentConnections.Count > 10)
            RecentConnections.RemoveRange(10, RecentConnections.Count - 10);
        Save();
    }
}
