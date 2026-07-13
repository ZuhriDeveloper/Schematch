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

    public string DisplayName =>
        string.IsNullOrEmpty(Database) ? Host : $"{Host} · {Database}";

    public ConnectionInfo Clone() => (ConnectionInfo)MemberwiseClone();
}
