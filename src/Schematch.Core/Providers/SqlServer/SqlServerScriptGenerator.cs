using System.Text;
using System.Text.RegularExpressions;
using Schematch.Core.Compare;
using Schematch.Core.Model;
using Schematch.Core.Scripting;

namespace Schematch.Core.Providers.SqlServer;

public sealed class SqlServerScriptGenerator : SyncScriptGeneratorBase
{
    private static string Q(string name) => $"[{name.Replace("]", "]]")}]";
    private static string Q(string schema, string name) => $"{Q(schema)}.{Q(name)}";

    protected override void EmitHeader(StringBuilder sb, SchemaComparisonResult comparison)
    {
        sb.AppendLine("-- Schematch deployment script (SQL Server)");
        sb.AppendLine($"-- Source: {comparison.Source.DatabaseName}   Target: {comparison.Target.DatabaseName}");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("-- Review carefully before executing against the target database.");
    }

    protected override void EmitTransactionStart(StringBuilder sb, ScriptOptions options)
    {
        sb.AppendLine();
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("SET NOCOUNT ON;");
        if (options.UseTransaction)
        {
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine("GO");
        }
    }

    protected override void EmitTransactionEnd(StringBuilder sb, ScriptOptions options)
    {
        if (options.UseTransaction)
        {
            sb.AppendLine();
            sb.AppendLine("COMMIT TRANSACTION;");
            sb.AppendLine("GO");
        }
    }

    protected override void EmitCreateSchema(StringBuilder sb, string schema)
    {
        sb.AppendLine($"IF SCHEMA_ID(N'{Esc(schema)}') IS NULL EXEC(N'CREATE SCHEMA {Q(schema)}');");
        sb.AppendLine("GO");
    }

    // CREATE OR ALTER handles every module kind on SQL Server 2016 SP1+.
    protected override bool NeedsDropBeforeCreate(CodeObjectModel source, CodeObjectModel target) => false;

    protected override void EmitDropCodeObject(StringBuilder sb, CodeObjectModel code)
    {
        string kind = code.Kind switch
        {
            CodeObjectKind.View => "VIEW",
            CodeObjectKind.Procedure => "PROCEDURE",
            CodeObjectKind.Function => "FUNCTION",
            CodeObjectKind.Trigger => "TRIGGER",
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };
        sb.AppendLine($"DROP {kind} IF EXISTS {Q(code.Schema, code.Name)};");
    }

    private static readonly Regex CreateKeyword = new(
        @"\bCREATE\s+(?=(OR\s+ALTER\s+)?(VIEW|PROC(EDURE)?|FUNCTION|TRIGGER)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    protected override void EmitCreateOrReplaceCodeObject(StringBuilder sb, CodeObjectModel code)
    {
        // Modules must be alone in their batch; rewrite the stored CREATE to CREATE OR ALTER.
        string definition = CreateKeyword.Replace(code.Definition,
            m => m.Value.Contains("ALTER", StringComparison.OrdinalIgnoreCase) ? m.Value : "CREATE OR ALTER ",
            count: 1);
        sb.AppendLine("GO");
        sb.AppendLine(definition.TrimEnd());
        sb.AppendLine("GO");
    }

    protected override void EmitDropForeignKey(StringBuilder sb, TableModel table, ForeignKeyModel fk) =>
        sb.AppendLine($"ALTER TABLE {Q(table.Schema, table.Name)} DROP CONSTRAINT {Q(fk.Name)};");

    protected override void EmitDropConstraint(StringBuilder sb, TableModel table, string constraintName) =>
        sb.AppendLine($"ALTER TABLE {Q(table.Schema, table.Name)} DROP CONSTRAINT {Q(constraintName)};");

    protected override void EmitAddKeyConstraint(StringBuilder sb, TableModel table, KeyConstraintModel key) =>
        sb.AppendLine($"ALTER TABLE {Q(table.Schema, table.Name)} ADD {KeyConstraintClause(key)};");

    protected override void EmitAddCheckConstraint(StringBuilder sb, TableModel table, CheckConstraintModel check) =>
        sb.AppendLine($"ALTER TABLE {Q(table.Schema, table.Name)} ADD CONSTRAINT {Q(check.Name)} CHECK {check.Expression};");

    protected override void EmitAddForeignKey(StringBuilder sb, TableModel table, ForeignKeyModel fk)
    {
        sb.Append($"ALTER TABLE {Q(table.Schema, table.Name)} ADD CONSTRAINT {Q(fk.Name)} FOREIGN KEY ");
        sb.Append($"({string.Join(", ", fk.Columns.Select(Q))}) ");
        sb.Append($"REFERENCES {Q(fk.ReferencedSchema, fk.ReferencedTable)} ({string.Join(", ", fk.ReferencedColumns.Select(Q))})");
        if (fk.OnDelete != "NO ACTION") sb.Append($" ON DELETE {fk.OnDelete}");
        if (fk.OnUpdate != "NO ACTION") sb.Append($" ON UPDATE {fk.OnUpdate}");
        sb.AppendLine(";");
    }

    protected override void EmitDropIndex(StringBuilder sb, TableModel table, IndexModel index) =>
        sb.AppendLine($"DROP INDEX {Q(index.Name)} ON {Q(table.Schema, table.Name)};");

    protected override void EmitCreateIndex(StringBuilder sb, TableModel table, IndexModel index)
    {
        sb.Append("CREATE ");
        if (index.IsUnique) sb.Append("UNIQUE ");
        if (index.IsClustered) sb.Append("CLUSTERED ");
        sb.Append($"INDEX {Q(index.Name)} ON {Q(table.Schema, table.Name)} ");
        sb.Append($"({string.Join(", ", index.Columns.Select(IndexCol))})");
        if (index.IncludedColumns.Count > 0)
            sb.Append($" INCLUDE ({string.Join(", ", index.IncludedColumns.Select(Q))})");
        if (!string.IsNullOrEmpty(index.FilterExpression))
            sb.Append($" WHERE {index.FilterExpression}");
        sb.AppendLine(";");

        static string IndexCol(IndexColumn c) => c.IsDescending ? $"{Q(c.Name)} DESC" : Q(c.Name);
    }

    protected override void EmitDropTable(StringBuilder sb, TableModel table) =>
        sb.AppendLine($"DROP TABLE {Q(table.Schema, table.Name)};");

    public override string ScriptCreateTable(TableModel table)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {Q(table.Schema, table.Name)} (");
        var parts = new List<string>();
        foreach (var col in table.Columns.OrderBy(c => c.Ordinal))
            parts.Add("    " + ColumnDefinition(col));
        if (table.PrimaryKey is not null)
            parts.Add("    " + KeyConstraintClause(table.PrimaryKey));
        foreach (var uq in table.UniqueConstraints)
            parts.Add("    " + KeyConstraintClause(uq));
        foreach (var ck in table.CheckConstraints)
            parts.Add($"    CONSTRAINT {Q(ck.Name)} CHECK {ck.Expression}");
        sb.AppendLine(string.Join(",\r\n", parts));
        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string KeyConstraintClause(KeyConstraintModel key)
    {
        string kind = key.IsPrimaryKey
            ? "PRIMARY KEY " + (key.IsClustered ? "CLUSTERED" : "NONCLUSTERED")
            : "UNIQUE" + (key.IsClustered ? " CLUSTERED" : "");
        string cols = string.Join(", ", key.Columns.Select(c => c.IsDescending ? $"{Q(c.Name)} DESC" : Q(c.Name)));
        return $"CONSTRAINT {Q(key.Name)} {kind} ({cols})";
    }

    internal static string ColumnDefinition(ColumnModel col)
    {
        if (col.ComputedExpression is not null)
            return $"{Q(col.Name)} AS {col.ComputedExpression}{(col.IsPersisted ? " PERSISTED" : "")}";

        var sb = new StringBuilder();
        sb.Append($"{Q(col.Name)} {col.DataType}");
        if (col.Collation is not null) sb.Append($" COLLATE {col.Collation}");
        if (col.IdentityClause is not null) sb.Append($" {col.IdentityClause}");
        sb.Append(col.IsNullable ? " NULL" : " NOT NULL");
        if (col.DefaultExpression is not null)
        {
            sb.Append(col.DefaultConstraintName is not null
                ? $" CONSTRAINT {Q(col.DefaultConstraintName)} DEFAULT {col.DefaultExpression}"
                : $" DEFAULT {col.DefaultExpression}");
        }
        return sb.ToString();
    }

    protected override void EmitAlterColumns(StringBuilder sb, TableDelta delta, ScriptOptions options)
    {
        string tableName = Q(delta.Source.Schema, delta.Source.Name);

        foreach (var col in delta.AddedColumns)
        {
            if (!col.IsNullable && col.DefaultExpression is null && col.ComputedExpression is null && col.IdentityClause is null)
                sb.AppendLine($"-- WARNING: adding NOT NULL column [{col.Name}] without a default fails if {delta.Source.FullName} has rows.");
            sb.AppendLine($"ALTER TABLE {tableName} ADD {ColumnDefinition(col)};");
        }

        foreach (var (src, tgt) in delta.ChangedColumns)
        {
            bool computedChanged = TextNormalizer.NormalizeExpression(src.ComputedExpression)
                                   != TextNormalizer.NormalizeExpression(tgt.ComputedExpression)
                                   || (src.ComputedExpression is not null && src.IsPersisted != tgt.IsPersisted);
            if (computedChanged)
            {
                sb.AppendLine($"-- Recreating computed column [{src.Name}] on {delta.Source.FullName}.");
                sb.AppendLine($"ALTER TABLE {tableName} DROP COLUMN {Q(src.Name)};");
                sb.AppendLine($"ALTER TABLE {tableName} ADD {ColumnDefinition(src)};");
                continue;
            }

            if (TextNormalizer.NormalizeExpression(src.IdentityClause) != TextNormalizer.NormalizeExpression(tgt.IdentityClause))
                sb.AppendLine($"-- WARNING: identity change on [{src.Name}] ({tgt.IdentityClause ?? "none"} → {src.IdentityClause ?? "none"}) cannot be scripted in place; requires table rebuild.");

            bool typeChanged = !string.Equals(src.DataType, tgt.DataType, StringComparison.OrdinalIgnoreCase);
            bool nullChanged = src.IsNullable != tgt.IsNullable;
            bool collationChanged = !string.Equals(src.Collation ?? "", tgt.Collation ?? "", StringComparison.OrdinalIgnoreCase);
            if (typeChanged || nullChanged || collationChanged)
            {
                if (typeChanged)
                    sb.AppendLine($"-- WARNING: verify {tgt.DataType} → {src.DataType} does not truncate data in [{src.Name}].");
                // A default constraint blocks ALTER COLUMN; drop it first and re-add below.
                if (tgt.DefaultConstraintName is not null)
                    sb.AppendLine($"ALTER TABLE {tableName} DROP CONSTRAINT {Q(tgt.DefaultConstraintName)};");
                string collate = src.Collation is not null ? $" COLLATE {src.Collation}" : "";
                sb.AppendLine($"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} {src.DataType}{collate} {(src.IsNullable ? "NULL" : "NOT NULL")};");
                if (src.DefaultExpression is not null)
                    EmitAddDefault(sb, tableName, src);
                continue;
            }

            // Only the default changed.
            if (TextNormalizer.NormalizeExpression(src.DefaultExpression) != TextNormalizer.NormalizeExpression(tgt.DefaultExpression))
            {
                if (tgt.DefaultConstraintName is not null)
                    sb.AppendLine($"ALTER TABLE {tableName} DROP CONSTRAINT {Q(tgt.DefaultConstraintName)};");
                if (src.DefaultExpression is not null)
                    EmitAddDefault(sb, tableName, src);
            }
        }

        if (options.IncludeDrops)
        {
            foreach (var col in delta.DroppedColumns)
            {
                sb.AppendLine($"-- WARNING: dropping column [{col.Name}] from {delta.Source.FullName} discards its data.");
                if (col.DefaultConstraintName is not null)
                    sb.AppendLine($"ALTER TABLE {tableName} DROP CONSTRAINT {Q(col.DefaultConstraintName)};");
                sb.AppendLine($"ALTER TABLE {tableName} DROP COLUMN {Q(col.Name)};");
            }
        }
    }

    private static void EmitAddDefault(StringBuilder sb, string tableName, ColumnModel col)
    {
        string name = col.DefaultConstraintName ?? $"DF_Schematch_{col.Name}";
        sb.AppendLine($"ALTER TABLE {tableName} ADD CONSTRAINT {Q(name)} DEFAULT {col.DefaultExpression} FOR {Q(col.Name)};");
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
