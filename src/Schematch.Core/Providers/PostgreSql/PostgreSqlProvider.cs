using System.Data.Common;
using Npgsql;
using Schematch.Core.Data;
using Schematch.Core.Model;
using Schematch.Core.Scripting;

namespace Schematch.Core.Providers.PostgreSql;

public sealed class PostgreSqlProvider : IDatabaseProvider
{
    public const string ProviderName = "PostgreSQL";

    public string Name => ProviderName;
    public bool SupportsWindowsAuth => false;
    public int DefaultPort => 5432;

    public ISyncScriptGenerator ScriptGenerator { get; } = new PostgreSqlScriptGenerator();
    public ISqlLiteralFormatter LiteralFormatter { get; } = new PostgreSqlLiteralFormatter();

    public string BuildConnectionString(ConnectionInfo info, string? databaseOverride = null)
    {
        NpgsqlConnectionStringBuilder b;
        if (info.UsesRawConnectionString)
        {
            b = new NpgsqlConnectionStringBuilder(info.ConnectionString);
            if (databaseOverride is not null) b.Database = databaseOverride;
            if (string.IsNullOrEmpty(b.ApplicationName)) b.ApplicationName = "Schematch";
            return b.ConnectionString;
        }

        b = new NpgsqlConnectionStringBuilder
        {
            Host = info.Host,
            Port = info.Port ?? DefaultPort,
            Database = databaseOverride ?? info.Database,
            Username = info.Username,
            Password = info.Password,
            Timeout = 15,
            ApplicationName = "Schematch",
        };
        return b.ConnectionString;
    }

    public DbConnection CreateConnection(ConnectionInfo info) =>
        new NpgsqlConnection(BuildConnectionString(info));

    public string ExtractDatabaseName(string connectionString)
    {
        try { return new NpgsqlConnectionStringBuilder(connectionString).Database ?? ""; }
        catch { return ""; }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionInfo info, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(info, "postgres"));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY datname", conn);
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public Task<DatabaseSchema> ReadSchemaAsync(ConnectionInfo info, IProgress<string>? progress = null, CancellationToken ct = default) =>
        new PostgreSqlSchemaReader(this).ReadAsync(info, progress, ct);

    public string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    // Npgsql parses multi-statement commands (dollar-quote aware); the script runs as one batch.
    public IReadOnlyList<string> SplitBatches(string script) =>
        string.IsNullOrWhiteSpace(script) ? Array.Empty<string>() : new[] { script };

    public string TransactionStartStatement => "BEGIN;";
    public string TransactionEndStatement => "COMMIT;";

    public string BuildKeyOrderBy(ColumnModel column)
    {
        string type = column.DataType.ToLowerInvariant();
        bool textual = type is "text" || type.StartsWith("character") || type.StartsWith("varchar") || type.StartsWith("char");
        // COLLATE "C" = byte order of UTF-8, aligning the server sort with ordinal comparison.
        return textual ? $"{QuoteIdentifier(column.Name)} COLLATE \"C\"" : QuoteIdentifier(column.Name);
    }

    public object NormalizeKeyValue(object value)
    {
        if (value is Guid g)
        {
            // PostgreSQL orders uuid by its RFC 4122 (big-endian) byte representation.
            Span<byte> bytes = stackalloc byte[16];
            g.TryWriteBytes(bytes, bigEndian: true, out _);
            return bytes.ToArray();
        }
        return value;
    }

    public string BuildInsert(TableModel table, IReadOnlyList<ColumnModel> columns, IReadOnlyList<string> literals)
    {
        // GENERATED ALWAYS identity columns reject explicit values unless overridden.
        string overriding = columns.Any(c => c.IdentityClause?.Contains("ALWAYS", StringComparison.OrdinalIgnoreCase) == true)
            ? "OVERRIDING SYSTEM VALUE " : "";
        return $"INSERT INTO {QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)} " +
               $"({string.Join(", ", columns.Select(c => QuoteIdentifier(c.Name)))}) {overriding}VALUES ({string.Join(", ", literals)});";
    }

    public string? IdentityInsertPrologue(TableModel table, IReadOnlyList<ColumnModel> insertColumns) => null;

    public string? IdentityInsertEpilogue(TableModel table, IReadOnlyList<ColumnModel> insertColumns) =>
        insertColumns.Any(c => c.IdentityClause is not null || c.DefaultExpression?.Contains("nextval", StringComparison.OrdinalIgnoreCase) == true)
            ? $"-- NOTE: resync the sequence for {table.Schema}.{table.Name} if needed, e.g. setval(pg_get_serial_sequence(...), max(id))."
            : null;
}
