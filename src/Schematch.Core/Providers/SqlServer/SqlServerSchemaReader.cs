using Microsoft.Data.SqlClient;
using Schematch.Core.Model;

namespace Schematch.Core.Providers.SqlServer;

/// <summary>Reads a full schema snapshot from SQL Server system catalog views.</summary>
internal sealed class SqlServerSchemaReader
{
    private readonly SqlServerProvider _provider;

    public SqlServerSchemaReader(SqlServerProvider provider) => _provider = provider;

    public async Task<DatabaseSchema> ReadAsync(ConnectionInfo info, IProgress<string>? progress, CancellationToken ct)
    {
        var schema = new DatabaseSchema
        {
            DatabaseName = info.Database,
            ProviderName = SqlServerProvider.ProviderName,
        };

        await using var conn = new SqlConnection(_provider.BuildConnectionString(info));
        await conn.OpenAsync(ct);

        progress?.Report("Reading schemas…");
        string dbCollation = await ReadDbCollationAsync(conn, ct);
        await ReadSchemasAsync(conn, schema, ct);

        progress?.Report("Reading tables and columns…");
        var tablesByName = await ReadTablesAndColumnsAsync(conn, schema, dbCollation, ct);

        progress?.Report("Reading keys and constraints…");
        await ReadKeyConstraintsAsync(conn, tablesByName, ct);
        await ReadIndexesAsync(conn, tablesByName, ct);
        await ReadForeignKeysAsync(conn, tablesByName, ct);
        await ReadCheckConstraintsAsync(conn, tablesByName, ct);

        progress?.Report("Reading views, procedures, functions, triggers…");
        await ReadCodeObjectsAsync(conn, schema, ct);

        return schema;
    }

    private static async Task<string> ReadDbCollationAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))", conn);
        return (string?)await cmd.ExecuteScalarAsync(ct) ?? "";
    }

    private static async Task ReadSchemasAsync(SqlConnection conn, DatabaseSchema schema, CancellationToken ct)
    {
        const string sql = """
            SELECT name FROM sys.schemas
            WHERE schema_id < 16384
              AND name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
              AND name NOT LIKE 'db[_]%'
            ORDER BY name
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            schema.Schemas.Add(reader.GetString(0));
    }

    private static async Task<Dictionary<string, TableModel>> ReadTablesAndColumnsAsync(
        SqlConnection conn, DatabaseSchema schema, string dbCollation, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS table_name,
                   c.name AS column_name, c.column_id,
                   tp.name AS type_name, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.collation_name,
                   CAST(ic.seed_value AS bigint) AS seed_value,
                   CAST(ic.increment_value AS bigint) AS increment_value,
                   dc.name AS default_name, dc.definition AS default_definition,
                   cc.definition AS computed_definition, cc.is_persisted
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id
            """;

        var tables = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string schemaName = reader.GetString(0);
            string tableName = reader.GetString(1);
            string key = $"{schemaName}.{tableName}";
            if (!tables.TryGetValue(key, out var table))
            {
                table = new TableModel { Schema = schemaName, Name = tableName };
                tables.Add(key, table);
                schema.Tables.Add(table);
            }

            string? collation = reader.IsDBNull(9) ? null : reader.GetString(9);
            var column = new ColumnModel
            {
                Name = reader.GetString(2),
                Ordinal = reader.GetInt32(3),
                DataType = FormatType(reader.GetString(4), reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7)),
                IsNullable = reader.GetBoolean(8),
                Collation = collation is not null && !collation.Equals(dbCollation, StringComparison.OrdinalIgnoreCase)
                    ? collation : null,
                DefaultConstraintName = reader.IsDBNull(12) ? null : reader.GetString(12),
                DefaultExpression = reader.IsDBNull(13) ? null : reader.GetString(13),
                ComputedExpression = reader.IsDBNull(14) ? null : reader.GetString(14),
                IsPersisted = !reader.IsDBNull(15) && reader.GetBoolean(15),
            };
            if (!reader.IsDBNull(10))
                column.IdentityClause = $"IDENTITY({reader.GetInt64(10)},{reader.GetInt64(11)})";
            table.Columns.Add(column);
        }
        return tables;
    }

    internal static string FormatType(string typeName, short maxLength, byte precision, byte scale) => typeName switch
    {
        "nvarchar" or "nchar" => maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength / 2})",
        "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength})",
        "decimal" or "numeric" => $"{typeName}({precision},{scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{typeName}({scale})",
        _ => typeName,
    };

    private static async Task ReadKeyConstraintsAsync(SqlConnection conn, Dictionary<string, TableModel> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS table_name,
                   kc.name AS constraint_name, kc.type AS constraint_type,
                   i.type_desc AS index_type,
                   col.name AS column_name, ic.key_ordinal, ic.is_descending_key
            FROM sys.key_constraints kc
            JOIN sys.tables t ON t.object_id = kc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE t.is_ms_shipped = 0 AND ic.is_included_column = 0
            ORDER BY s.name, t.name, kc.name, ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        KeyConstraintModel? current = null;
        string currentKey = "";
        while (await reader.ReadAsync(ct))
        {
            string tableKey = $"{reader.GetString(0)}.{reader.GetString(1)}";
            string name = reader.GetString(2);
            if (current is null || currentKey != $"{tableKey}|{name}")
            {
                current = new KeyConstraintModel
                {
                    Name = name,
                    IsPrimaryKey = reader.GetString(3).Trim() == "PK",
                    IsClustered = reader.GetString(4) == "CLUSTERED",
                };
                currentKey = $"{tableKey}|{name}";
                if (tables.TryGetValue(tableKey, out var table))
                {
                    if (current.IsPrimaryKey) table.PrimaryKey = current;
                    else table.UniqueConstraints.Add(current);
                }
            }
            current.Columns.Add(new IndexColumn { Name = reader.GetString(5), IsDescending = reader.GetBoolean(7) });
        }
    }

    private static async Task ReadIndexesAsync(SqlConnection conn, Dictionary<string, TableModel> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS table_name,
                   i.name AS index_name, i.is_unique, i.type_desc, i.filter_definition,
                   col.name AS column_name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE t.is_ms_shipped = 0
              AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND i.type > 0 AND i.is_hypothetical = 0
            ORDER BY s.name, t.name, i.name, ic.is_included_column, ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        IndexModel? current = null;
        string currentKey = "";
        while (await reader.ReadAsync(ct))
        {
            string tableKey = $"{reader.GetString(0)}.{reader.GetString(1)}";
            string name = reader.GetString(2);
            if (current is null || currentKey != $"{tableKey}|{name}")
            {
                current = new IndexModel
                {
                    Name = name,
                    IsUnique = reader.GetBoolean(3),
                    IsClustered = reader.GetString(4) == "CLUSTERED",
                    FilterExpression = reader.IsDBNull(5) ? null : reader.GetString(5),
                };
                currentKey = $"{tableKey}|{name}";
                if (tables.TryGetValue(tableKey, out var table))
                    table.Indexes.Add(current);
            }
            if (reader.GetBoolean(9))
                current.IncludedColumns.Add(reader.GetString(6));
            else
                current.Columns.Add(new IndexColumn { Name = reader.GetString(6), IsDescending = reader.GetBoolean(8) });
        }
    }

    private static async Task ReadForeignKeysAsync(SqlConnection conn, Dictionary<string, TableModel> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS table_name, fk.name AS fk_name,
                   pc.name AS column_name,
                   rs.name AS ref_schema, rt.name AS ref_table, rc.name AS ref_column,
                   REPLACE(fk.delete_referential_action_desc, '_', ' ') AS on_delete,
                   REPLACE(fk.update_referential_action_desc, '_', ' ') AS on_update
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, fk.name, fkc.constraint_column_id
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        ForeignKeyModel? current = null;
        string currentKey = "";
        while (await reader.ReadAsync(ct))
        {
            string tableKey = $"{reader.GetString(0)}.{reader.GetString(1)}";
            string name = reader.GetString(2);
            if (current is null || currentKey != $"{tableKey}|{name}")
            {
                current = new ForeignKeyModel
                {
                    Name = name,
                    ReferencedSchema = reader.GetString(4),
                    ReferencedTable = reader.GetString(5),
                    OnDelete = reader.GetString(7),
                    OnUpdate = reader.GetString(8),
                };
                currentKey = $"{tableKey}|{name}";
                if (tables.TryGetValue(tableKey, out var table))
                    table.ForeignKeys.Add(current);
            }
            current.Columns.Add(reader.GetString(3));
            current.ReferencedColumns.Add(reader.GetString(6));
        }
    }

    private static async Task ReadCheckConstraintsAsync(SqlConnection conn, Dictionary<string, TableModel> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS table_name, cc.name, cc.definition
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, cc.name
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table))
                table.CheckConstraints.Add(new CheckConstraintModel
                {
                    Name = reader.GetString(2),
                    Expression = reader.GetString(3),
                });
        }
    }

    private static async Task ReadCodeObjectsAsync(SqlConnection conn, DatabaseSchema schema, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name, o.type, sm.definition,
                   ps.name + '.' + pt.name AS parent_table
            FROM sys.sql_modules sm
            JOIN sys.objects o ON o.object_id = sm.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.tables pt ON pt.object_id = o.parent_object_id
            LEFT JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR')
              AND sm.definition IS NOT NULL
              AND (o.type <> 'TR' OR pt.object_id IS NOT NULL)
            ORDER BY s.name, o.name
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string type = reader.GetString(2).Trim();
            schema.CodeObjects.Add(new CodeObjectModel
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = type switch
                {
                    "V" => CodeObjectKind.View,
                    "P" => CodeObjectKind.Procedure,
                    "TR" => CodeObjectKind.Trigger,
                    _ => CodeObjectKind.Function,
                },
                Definition = reader.GetString(3),
                ParentTable = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }
    }
}
