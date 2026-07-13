using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Schematch.Core.Data;
using Schematch.Core.Model;
using Schematch.Core.Scripting;

namespace Schematch.Core.Providers.SqlServer;

public sealed class SqlServerProvider : IDatabaseProvider
{
    public const string ProviderName = "SQL Server";

    public string Name => ProviderName;
    public bool SupportsWindowsAuth => true;
    public int DefaultPort => 1433;

    public ISyncScriptGenerator ScriptGenerator { get; } = new SqlServerScriptGenerator();
    public ISqlLiteralFormatter LiteralFormatter { get; } = new SqlServerLiteralFormatter();

    public string BuildConnectionString(ConnectionInfo info, string? databaseOverride = null)
    {
        SqlConnectionStringBuilder b;
        if (info.UsesRawConnectionString)
        {
            b = new SqlConnectionStringBuilder(info.ConnectionString);
            if (databaseOverride is not null) b.InitialCatalog = databaseOverride;
            // SqlConnectionStringBuilder.ContainsKey reports true for every known keyword and
            // ApplicationName has a non-empty default, so scan the user's raw string directly.
            if (!HasKeyword(info.ConnectionString!, "Application Name", "app")) b.ApplicationName = "Schematch";
            return b.ConnectionString;
        }

        b = new SqlConnectionStringBuilder
        {
            DataSource = info.Port is int port and not 1433 ? $"{info.Host},{port}" : info.Host,
            InitialCatalog = databaseOverride ?? info.Database,
            IntegratedSecurity = info.UseWindowsAuth,
            TrustServerCertificate = info.TrustServerCertificate,
            ConnectTimeout = 15,
            ApplicationName = "Schematch",
        };
        if (!info.UseWindowsAuth)
        {
            b.UserID = info.Username;
            b.Password = info.Password;
        }
        return b.ConnectionString;
    }

    public DbConnection CreateConnection(ConnectionInfo info) =>
        new SqlConnection(BuildConnectionString(info));

    public string ExtractDatabaseName(string connectionString)
    {
        try { return new SqlConnectionStringBuilder(connectionString).InitialCatalog; }
        catch { return ""; }
    }

    private static bool HasKeyword(string connectionString, params string[] keys)
    {
        foreach (var part in connectionString.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim();
            if (keys.Any(k => string.Equals(key, k, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionInfo info, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(BuildConnectionString(info, "master"));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name", conn);
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public Task<DatabaseSchema> ReadSchemaAsync(ConnectionInfo info, IProgress<string>? progress = null, CancellationToken ct = default) =>
        new SqlServerSchemaReader(this).ReadAsync(info, progress, ct);

    public string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";

    public string TransactionStartStatement => "SET XACT_ABORT ON;\r\nBEGIN TRANSACTION;";
    public string TransactionEndStatement => "COMMIT TRANSACTION;";

    private static readonly string[] TextTypes = { "char", "varchar", "nchar", "nvarchar", "sysname" };

    public string BuildKeyOrderBy(ColumnModel column)
    {
        string baseType = BaseTypeName(column.DataType);
        if (TextTypes.Contains(baseType))
            return $"{QuoteIdentifier(column.Name)} COLLATE Latin1_General_BIN2";
        if (baseType == "uniqueidentifier")
            // SQL Server orders GUIDs by byte groups; binary(16) cast matches Guid.ToByteArray() order.
            return $"CAST({QuoteIdentifier(column.Name)} AS binary(16))";
        return QuoteIdentifier(column.Name);
    }

    public object NormalizeKeyValue(object value) =>
        value is Guid g ? g.ToByteArray() : value;

    public string BuildInsert(TableModel table, IReadOnlyList<ColumnModel> columns, IReadOnlyList<string> literals) =>
        $"INSERT INTO {QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)} " +
        $"({string.Join(", ", columns.Select(c => QuoteIdentifier(c.Name)))}) VALUES ({string.Join(", ", literals)});";

    public string? IdentityInsertPrologue(TableModel table, IReadOnlyList<ColumnModel> insertColumns) =>
        insertColumns.Any(c => c.IdentityClause is not null)
            ? $"SET IDENTITY_INSERT {QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)} ON;"
            : null;

    public string? IdentityInsertEpilogue(TableModel table, IReadOnlyList<ColumnModel> insertColumns) =>
        insertColumns.Any(c => c.IdentityClause is not null)
            ? $"SET IDENTITY_INSERT {QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)} OFF;"
            : null;

    private static string BaseTypeName(string dataType)
    {
        int paren = dataType.IndexOf('(');
        return (paren < 0 ? dataType : dataType[..paren]).Trim().ToLowerInvariant();
    }

    private static readonly Regex GoLine =
        new(@"^\s*GO\s*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> SplitBatches(string script)
    {
        var batches = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            if (GoLine.IsMatch(line))
            {
                Flush();
                continue;
            }
            current.AppendLine(line);
        }
        Flush();
        return batches;

        void Flush()
        {
            string batch = current.ToString().Trim();
            current.Clear();
            // Skip empty batches and batches that are only comments.
            if (batch.Length > 0 && batch.Split('\n').Any(l => l.Trim() is { Length: > 0 } t && !t.StartsWith("--")))
                batches.Add(batch);
        }
    }
}
