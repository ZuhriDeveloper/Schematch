using System.Data.Common;
using Schematch.Core.Data;
using Schematch.Core.Model;
using Schematch.Core.Scripting;

namespace Schematch.Core.Providers;

public interface IDatabaseProvider
{
    string Name { get; }
    bool SupportsWindowsAuth { get; }
    int DefaultPort { get; }

    string BuildConnectionString(ConnectionInfo info, string? databaseOverride = null);
    DbConnection CreateConnection(ConnectionInfo info);

    /// <summary>Parses the database/catalog name out of a raw connection string (empty if absent/invalid).</summary>
    string ExtractDatabaseName(string connectionString);

    Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionInfo info, CancellationToken ct = default);

    /// <summary>User schemas in the connection's database — for the schema-scope picker.</summary>
    Task<IReadOnlyList<string>> ListSchemasAsync(ConnectionInfo info, CancellationToken ct = default);

    Task<DatabaseSchema> ReadSchemaAsync(ConnectionInfo info, IProgress<string>? progress = null, CancellationToken ct = default);

    ISyncScriptGenerator ScriptGenerator { get; }
    ISqlLiteralFormatter LiteralFormatter { get; }

    string QuoteIdentifier(string name);

    /// <summary>Splits a script into executable batches (SQL Server: on GO lines; PostgreSQL: single batch).</summary>
    IReadOnlyList<string> SplitBatches(string script);

    string TransactionStartStatement { get; }
    string TransactionEndStatement { get; }

    /// <summary>
    /// ORDER BY expression for a data-compare key column. Text columns get a binary collation so the
    /// server's sort order matches the client's ordinal comparison during the merge join.
    /// </summary>
    string BuildKeyOrderBy(ColumnModel column);

    /// <summary>Normalizes a key value read from the database so client-side comparison matches the server's ORDER BY (e.g. SQL Server GUIDs).</summary>
    object NormalizeKeyValue(object value);

    /// <summary>INSERT statement in the provider dialect (handles identity/generated column clauses).</summary>
    string BuildInsert(TableModel table, IReadOnlyList<ColumnModel> columns, IReadOnlyList<string> literals);

    /// <summary>Statement required before explicit-value inserts into an identity column, or null (SQL Server: SET IDENTITY_INSERT ON).</summary>
    string? IdentityInsertPrologue(TableModel table, IReadOnlyList<ColumnModel> insertColumns);
    string? IdentityInsertEpilogue(TableModel table, IReadOnlyList<ColumnModel> insertColumns);
}
