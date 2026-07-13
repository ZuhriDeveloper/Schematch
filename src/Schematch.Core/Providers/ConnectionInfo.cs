namespace Schematch.Core.Providers;

public sealed class ConnectionInfo
{
    public string ProviderName { get; set; } = "SQL Server";

    /// <summary>Server/host name; for LocalDB e.g. "(localdb)\MSSQLLocalDB".</summary>
    public string Host { get; set; } = "";

    /// <summary>Port for PostgreSQL; null uses the provider default.</summary>
    public int? Port { get; set; }

    public string Database { get; set; } = "";

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

    public string DisplayName => UsesRawConnectionString
        ? (string.IsNullOrEmpty(Database) ? "(connection string)" : $"{Database} (connection string)")
        : string.IsNullOrEmpty(Database) ? Host : $"{Host} · {Database}";

    public ConnectionInfo Clone() => (ConnectionInfo)MemberwiseClone();
}
