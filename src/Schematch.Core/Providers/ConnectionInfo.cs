namespace Schematch.Core.Providers;

public sealed class ConnectionInfo
{
    public string ProviderName { get; set; } = "SQL Server";

    /// <summary>Server/host name; for LocalDB e.g. "(localdb)\MSSQLLocalDB".</summary>
    public string Host { get; set; } = "";

    /// <summary>Port for PostgreSQL; null uses the provider default.</summary>
    public int? Port { get; set; }

    public string Database { get; set; } = "";

    /// <summary>
    /// Limit the comparison to a single schema (PostgreSQL). Empty compares every user schema.
    /// Ignored by SQL Server.
    /// </summary>
    public string Schema { get; set; } = "";

    /// <summary>Windows integrated auth (SQL Server only).</summary>
    public bool UseWindowsAuth { get; set; } = true;

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>
    /// When set, providers connect with this verbatim string and ignore the structured fields above.
    /// The engine is still chosen by <see cref="ProviderName"/>. <see cref="Database"/> is populated
    /// from the string (via the provider) so display, confirmations, and dedupe still work.
    /// </summary>
    public string? ConnectionString { get; set; }

    public bool UsesRawConnectionString => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Optional user-chosen nickname (e.g. "Billing DEV") shown instead of the host/database so
    /// source and target are easy to tell apart. Display only — never used to connect.
    /// </summary>
    public string Alias { get; set; } = "";

    public bool HasAlias => !string.IsNullOrWhiteSpace(Alias);

    /// <summary>Where the connection points: host, database and schema scope.</summary>
    public string Description
    {
        get
        {
            string schemaSuffix = string.IsNullOrEmpty(Schema) ? "" : $" [{Schema}]";
            return UsesRawConnectionString
                ? (string.IsNullOrEmpty(Database) ? "(connection string)" : $"{Database}{schemaSuffix} (connection string)")
                : (string.IsNullOrEmpty(Database) ? Host : $"{Host} · {Database}{schemaSuffix}");
        }
    }

    /// <summary>Alias plus where it points, e.g. "Billing DEV (localhost · billing)"; just the description without an alias.</summary>
    public string DisplayName => HasAlias ? $"{Alias.Trim()} ({Description})" : Description;

    /// <summary>Compact label: the alias when set, otherwise the description.</summary>
    public string ShortDisplayName => HasAlias ? Alias.Trim() : Description;

    public ConnectionInfo Clone() => (ConnectionInfo)MemberwiseClone();
}
