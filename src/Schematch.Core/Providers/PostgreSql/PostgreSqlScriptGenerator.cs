using System.Text;
using Schematch.Core.Compare;
using Schematch.Core.Model;
using Schematch.Core.Scripting;

namespace Schematch.Core.Providers.PostgreSql;

public sealed class PostgreSqlScriptGenerator : SyncScriptGeneratorBase
{
    private static string Q(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
    private static string Q(string schema, string name) => $"{Q(schema)}.{Q(name)}";

    protected override void EmitHeader(StringBuilder sb, SchemaComparisonResult comparison)
    {
        sb.AppendLine("-- Schematch deployment script (PostgreSQL)");
        sb.AppendLine($"-- Source: {comparison.Source.DatabaseName}   Target: {comparison.Target.DatabaseName}");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("-- Review carefully before executing against the target database.");
    }

    protected override void EmitTransactionStart(StringBuilder sb, ScriptOptions options)
    {
        sb.AppendLine();
        if (options.UseTransaction) sb.AppendLine("BEGIN;");
    }

    protected override void EmitTransactionEnd(StringBuilder sb, ScriptOptions options)
    {
        if (options.UseTransaction)
        {
            sb.AppendLine();
            sb.AppendLine("COMMIT;");
        }
    }

    protected override void EmitCreateSchema(StringBuilder sb, string schema) =>
        sb.AppendLine($"CREATE SCHEMA IF NOT EXISTS {Q(schema)};");

    // CREATE OR REPLACE VIEW/FUNCTION fails on column list changes or return-type changes;
    // dropping first is the only always-safe path, and triggers have no OR REPLACE before PG 14.
    protected override bool NeedsDropBeforeCreate(CodeObjectModel source, CodeObjectModel target) => true;

    protected override void EmitDropCodeObject(StringBuilder sb, CodeObjectModel code)
    {
        switch (code.Kind)
        {
            case CodeObjectKind.View:
                sb.AppendLine($"DROP VIEW IF EXISTS {Q(code.Schema, code.Name)};");
                break;
            case CodeObjectKind.Trigger:
                sb.AppendLine($"DROP TRIGGER IF EXISTS {Q(code.Name)} ON {QualifyParent(code)};");
                break;
            case CodeObjectKind.Function:
                sb.AppendLine($"DROP FUNCTION IF EXISTS {RoutineReference(code)};");
                break;
            case CodeObjectKind.Procedure:
                sb.AppendLine($"DROP PROCEDURE IF EXISTS {RoutineReference(code)};");
                break;
        }
    }

    /// <summary>Function/procedure names carry their identity arguments: "fn(integer, text)".</summary>
    private static string RoutineReference(CodeObjectModel code)
    {
        int paren = code.Name.IndexOf('(');
        string bareName = paren < 0 ? code.Name : code.Name[..paren];
        string args = paren < 0 ? "()" : code.Name[paren..];
        return $"{Q(code.Schema)}.{Q(bareName)}{args}";
    }

    private static string QualifyParent(CodeObjectModel code)
    {
        var parts = (code.ParentTable ?? "").Split('.', 2);
        return parts.Length == 2 ? Q(parts[0], parts[1]) : Q(code.ParentTable ?? "");
    }

    protected override void EmitCreateOrReplaceCodeObject(StringBuilder sb, CodeObjectModel code)
    {
        string definition = code.Definition.TrimEnd();
        if (!definition.EndsWith(';')) definition += ";";
        sb.AppendLine(definition);
    }

    protected override void EmitDropForeignKey(StringBuilder sb, TableModel table, ForeignKeyModel fk) =>
        sb.AppendLine($"ALTER TABLE {Q(table.Schema, table.Name)} DROP CONSTRAINT IF EXISTS {Q(fk.Name)};");

    protected override void EmitDropConstraint(StringBuilder sb, TableModel table, string constraintName) =>
        sb.AppendLine($"ALTER TABLE {Q(table.Schema, table.Name)} DROP CONSTRAINT IF EXISTS {Q(constraintName)};");

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
        sb.AppendLine($"DROP INDEX IF EXISTS {Q(table.Schema)}.{Q(index.Name)};");

    protected override void EmitCreateIndex(StringBuilder sb, TableModel table, IndexModel index)
    {
        if (index.RawDefinition is not null)
        {
            string def = index.RawDefinition.TrimEnd();
            sb.AppendLine(def.EndsWith(';') ? def : def + ";");
        }
        else
        {
            sb.Append($"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX {Q(index.Name)} ON {Q(table.Schema, table.Name)} ");
            sb.Append($"({string.Join(", ", index.Columns.Select(c => c.IsDescending ? $"{Q(c.Name)} DESC" : Q(c.Name)))})");
            if (!string.IsNullOrEmpty(index.FilterExpression)) sb.Append($" WHERE {index.FilterExpression}");
            sb.AppendLine(";");
        }
    }

    protected override void EmitDropTable(StringBuilder sb, TableModel table) =>
        sb.AppendLine($"DROP TABLE IF EXISTS {Q(table.Schema, table.Name)};");

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
        sb.AppendLine(string.Join(",\n", parts));
        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string KeyConstraintClause(KeyConstraintModel key)
    {
        string cols = string.Join(", ", key.Columns.Select(c => Q(c.Name)));
        return $"CONSTRAINT {Q(key.Name)} {(key.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE")} ({cols})";
    }

    internal static string ColumnDefinition(ColumnModel col)
    {
        if (col.ComputedExpression is not null)
            return $"{Q(col.Name)} {col.DataType} GENERATED ALWAYS AS ({TrimParens(col.ComputedExpression)}) STORED";

        var sb = new StringBuilder();
        sb.Append($"{Q(col.Name)} {col.DataType}");
        if (col.Collation is not null) sb.Append($" COLLATE {Q(col.Collation)}");
        if (col.IdentityClause is not null) sb.Append($" {col.IdentityClause}");
        if (!col.IsNullable) sb.Append(" NOT NULL");
        if (col.DefaultExpression is not null) sb.Append($" DEFAULT {col.DefaultExpression}");
        return sb.ToString();
    }

    private static string TrimParens(string expr)
    {
        string s = expr.Trim();
        return s.StartsWith('(') && s.EndsWith(')') ? s[1..^1] : s;
    }

    protected override void EmitAlterColumns(StringBuilder sb, TableDelta delta, ScriptOptions options)
    {
        string tableName = Q(delta.Source.Schema, delta.Source.Name);

        foreach (var col in delta.AddedColumns)
        {
            if (!col.IsNullable && col.DefaultExpression is null && col.ComputedExpression is null && col.IdentityClause is null)
                sb.AppendLine($"-- WARNING: adding NOT NULL column \"{col.Name}\" without a default fails if {delta.Source.FullName} has rows.");
            sb.AppendLine($"ALTER TABLE {tableName} ADD COLUMN {ColumnDefinition(col)};");
        }

        foreach (var (src, tgt) in delta.ChangedColumns)
        {
            bool generatedChanged = TextNormalizer.NormalizeExpression(src.ComputedExpression)
                                    != TextNormalizer.NormalizeExpression(tgt.ComputedExpression);
            if (generatedChanged)
            {
                sb.AppendLine($"-- Recreating generated column \"{src.Name}\" on {delta.Source.FullName}.");
                sb.AppendLine($"ALTER TABLE {tableName} DROP COLUMN {Q(src.Name)};");
                sb.AppendLine($"ALTER TABLE {tableName} ADD COLUMN {ColumnDefinition(src)};");
                continue;
            }

            if (!string.Equals(src.DataType, tgt.DataType, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"-- WARNING: verify the cast {tgt.DataType} → {src.DataType} preserves the data in \"{src.Name}\".");
                sb.AppendLine($"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} TYPE {src.DataType} USING {Q(src.Name)}::{src.DataType};");
            }

            if (src.IsNullable != tgt.IsNullable)
                sb.AppendLine($"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} {(src.IsNullable ? "DROP NOT NULL" : "SET NOT NULL")};");

            if (TextNormalizer.NormalizeExpression(src.DefaultExpression) != TextNormalizer.NormalizeExpression(tgt.DefaultExpression))
            {
                sb.AppendLine(src.DefaultExpression is null
                    ? $"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} DROP DEFAULT;"
                    : $"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} SET DEFAULT {src.DefaultExpression};");
            }

            if (TextNormalizer.NormalizeExpression(src.IdentityClause) != TextNormalizer.NormalizeExpression(tgt.IdentityClause))
            {
                if (src.IdentityClause is null)
                    sb.AppendLine($"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} DROP IDENTITY IF EXISTS;");
                else if (tgt.IdentityClause is null)
                    sb.AppendLine($"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} ADD {src.IdentityClause};");
                else
                    sb.AppendLine($"ALTER TABLE {tableName} ALTER COLUMN {Q(src.Name)} SET GENERATED {(src.IdentityClause.Contains("ALWAYS") ? "ALWAYS" : "BY DEFAULT")};");
            }
        }

        if (options.IncludeDrops)
        {
            foreach (var col in delta.DroppedColumns)
            {
                sb.AppendLine($"-- WARNING: dropping column \"{col.Name}\" from {delta.Source.FullName} discards its data.");
                sb.AppendLine($"ALTER TABLE {tableName} DROP COLUMN {Q(col.Name)};");
            }
        }
    }
}
