using System.Data;
using System.Data.Common;
using System.Text;
using Schematch.Core.Model;
using Schematch.Core.Providers;

namespace Schematch.Core.Data;

public sealed class DataCompareOptions
{
    /// <summary>Emit DELETE for rows that exist only in the target.</summary>
    public bool IncludeDeletes { get; set; } = true;

    /// <summary>How many statements to keep as an on-screen preview per table.</summary>
    public int MaxSampleRows { get; set; } = 100;
}

public sealed class TableDataDiff
{
    public required string TableName { get; init; }
    public List<string> KeyColumns { get; } = new();
    public long MissingInTarget { get; set; }
    public long ExtraInTarget { get; set; }
    public long DifferentRows { get; set; }
    public long EqualRows { get; set; }

    /// <summary>Full DML script for this table (deletes → updates → inserts).</summary>
    public string Script { get; set; } = "";

    /// <summary>First N statements for the preview grid.</summary>
    public List<string> Samples { get; } = new();

    /// <summary>Why this table could not be compared (no PK, column mismatch...).</summary>
    public string? Error { get; set; }

    public bool HasChanges => MissingInTarget + ExtraInTarget + DifferentRows > 0;
}

/// <summary>
/// Row-data comparison via streaming merge join: both sides are read ordered by primary key
/// (binary collation) and walked in lockstep, so large tables never load into memory.
/// </summary>
public static class DataComparer
{
    public static async Task<TableDataDiff> CompareTableAsync(
        IDatabaseProvider provider,
        ConnectionInfo sourceInfo, ConnectionInfo targetInfo,
        TableModel sourceTable, TableModel targetTable,
        DataCompareOptions options, CancellationToken ct = default)
    {
        var result = new TableDataDiff { TableName = sourceTable.FullName };

        if (sourceTable.PrimaryKey is null)
        {
            result.Error = "No primary key — rows cannot be matched.";
            return result;
        }

        var keyNames = sourceTable.PrimaryKey.Columns.Select(c => c.Name).ToList();
        result.KeyColumns.AddRange(keyNames);

        var targetColNames = new HashSet<string>(targetTable.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        if (keyNames.Any(k => !targetColNames.Contains(k)))
        {
            result.Error = "Primary key columns are missing in the target — sync the schema first.";
            return result;
        }

        // Keys first, then every comparable source column present on both sides.
        // Computed columns are excluded: they can't be written and follow their expression.
        var keyCols = keyNames
            .Select(k => sourceTable.Columns.First(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var valueCols = sourceTable.Columns
            .Where(c => c.ComputedExpression is null
                        && !keyNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)
                        && targetColNames.Contains(c.Name))
            .OrderBy(c => c.Ordinal)
            .ToList();
        var allCols = keyCols.Concat(valueCols).ToList();

        string select = BuildSelect(provider, sourceTable, allCols, keyCols);

        var inserts = new StringBuilder();
        var updates = new StringBuilder();
        var deletes = new StringBuilder();

        await using var sourceConn = provider.CreateConnection(sourceInfo);
        await using var targetConn = provider.CreateConnection(targetInfo);
        await sourceConn.OpenAsync(ct);
        await targetConn.OpenAsync(ct);

        await using var sourceCmd = CreateCommand(sourceConn, select);
        await using var targetCmd = CreateCommand(targetConn, select);
        await using var rs = await sourceCmd.ExecuteReaderAsync(ct);
        await using var rt = await targetCmd.ExecuteReaderAsync(ct);

        bool hasS = await rs.ReadAsync(ct);
        bool hasT = await rt.ReadAsync(ct);

        while (hasS || hasT)
        {
            ct.ThrowIfCancellationRequested();
            int cmp = (hasS, hasT) switch
            {
                (true, false) => -1,
                (false, true) => 1,
                _ => CompareKeys(provider, rs, rt, keyCols.Count),
            };

            if (cmp < 0)
            {
                result.MissingInTarget++;
                string stmt = provider.BuildInsert(sourceTable, allCols,
                    allCols.Select((_, i) => provider.LiteralFormatter.Format(rs.GetValue(i))).ToList());
                inserts.AppendLine(stmt);
                AddSample(result, options, stmt);
                hasS = await rs.ReadAsync(ct);
            }
            else if (cmp > 0)
            {
                result.ExtraInTarget++;
                if (options.IncludeDeletes)
                {
                    string stmt = BuildDelete(provider, sourceTable, keyCols, rt);
                    deletes.AppendLine(stmt);
                    AddSample(result, options, stmt);
                }
                hasT = await rt.ReadAsync(ct);
            }
            else
            {
                var changed = new List<int>();
                for (int i = keyCols.Count; i < allCols.Count; i++)
                {
                    if (!ValuesEqual(rs.GetValue(i), rt.GetValue(i)))
                        changed.Add(i);
                }
                if (changed.Count > 0)
                {
                    result.DifferentRows++;
                    string stmt = BuildUpdate(provider, sourceTable, allCols, keyCols, rs, changed);
                    updates.AppendLine(stmt);
                    AddSample(result, options, stmt);
                }
                else
                {
                    result.EqualRows++;
                }
                hasS = await rs.ReadAsync(ct);
                hasT = await rt.ReadAsync(ct);
            }
        }

        result.Script = AssembleScript(provider, sourceTable, allCols, deletes, updates, inserts);
        return result;
    }

    private static DbCommand CreateCommand(DbConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        return cmd;
    }

    private static string BuildSelect(IDatabaseProvider provider, TableModel table,
        List<ColumnModel> allCols, List<ColumnModel> keyCols)
    {
        string cols = string.Join(", ", allCols.Select(c => provider.QuoteIdentifier(c.Name)));
        string orderBy = string.Join(", ", keyCols.Select(provider.BuildKeyOrderBy));
        return $"SELECT {cols} FROM {provider.QuoteIdentifier(table.Schema)}.{provider.QuoteIdentifier(table.Name)} ORDER BY {orderBy}";
    }

    private static int CompareKeys(IDatabaseProvider provider, DbDataReader a, DbDataReader b, int keyCount)
    {
        for (int i = 0; i < keyCount; i++)
        {
            int c = CompareValues(provider.NormalizeKeyValue(a.GetValue(i)), provider.NormalizeKeyValue(b.GetValue(i)));
            if (c != 0) return c;
        }
        return 0;
    }

    private static int CompareValues(object a, object b)
    {
        bool an = a is DBNull, bn = b is DBNull;
        if (an && bn) return 0;
        if (an) return -1;
        if (bn) return 1;

        if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
        if (a is byte[] ba && b is byte[] bb)
        {
            int len = Math.Min(ba.Length, bb.Length);
            for (int i = 0; i < len; i++)
            {
                int c = ba[i].CompareTo(bb[i]);
                if (c != 0) return c;
            }
            return ba.Length.CompareTo(bb.Length);
        }
        if (a is IComparable ca && a.GetType() == b.GetType()) return ca.CompareTo(b);
        return Comparer<object>.Default.Compare(a, b);
    }

    private static bool ValuesEqual(object a, object b)
    {
        if (a is DBNull && b is DBNull) return true;
        if (a is DBNull || b is DBNull) return false;
        if (a is byte[] ba && b is byte[] bb) return ba.AsSpan().SequenceEqual(bb);
        return a.Equals(b);
    }

    private static string BuildDelete(IDatabaseProvider provider, TableModel table,
        List<ColumnModel> keyCols, DbDataReader row)
    {
        string where = BuildKeyPredicate(provider, keyCols, row);
        return $"DELETE FROM {provider.QuoteIdentifier(table.Schema)}.{provider.QuoteIdentifier(table.Name)} WHERE {where};";
    }

    private static string BuildUpdate(IDatabaseProvider provider, TableModel table,
        List<ColumnModel> allCols, List<ColumnModel> keyCols, DbDataReader sourceRow, List<int> changedOrdinals)
    {
        string set = string.Join(", ", changedOrdinals.Select(i =>
            $"{provider.QuoteIdentifier(allCols[i].Name)} = {provider.LiteralFormatter.Format(sourceRow.GetValue(i))}"));
        string where = BuildKeyPredicate(provider, keyCols, sourceRow);
        return $"UPDATE {provider.QuoteIdentifier(table.Schema)}.{provider.QuoteIdentifier(table.Name)} SET {set} WHERE {where};";
    }

    private static string BuildKeyPredicate(IDatabaseProvider provider, List<ColumnModel> keyCols, DbDataReader row) =>
        string.Join(" AND ", keyCols.Select((c, i) =>
            $"{provider.QuoteIdentifier(c.Name)} = {provider.LiteralFormatter.Format(row.GetValue(i))}"));

    private static void AddSample(TableDataDiff result, DataCompareOptions options, string statement)
    {
        if (result.Samples.Count < options.MaxSampleRows)
            result.Samples.Add(statement);
    }

    private static string AssembleScript(IDatabaseProvider provider, TableModel table,
        List<ColumnModel> allCols, StringBuilder deletes, StringBuilder updates, StringBuilder inserts)
    {
        if (deletes.Length == 0 && updates.Length == 0 && inserts.Length == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine($"-- ==== Data changes for {table.FullName} ====");
        if (deletes.Length > 0) sb.Append(deletes);
        if (updates.Length > 0) sb.Append(updates);
        if (inserts.Length > 0)
        {
            string? prologue = provider.IdentityInsertPrologue(table, allCols);
            string? epilogue = provider.IdentityInsertEpilogue(table, allCols);
            if (prologue is not null) sb.AppendLine(prologue);
            sb.Append(inserts);
            if (epilogue is not null) sb.AppendLine(epilogue);
        }
        return sb.ToString();
    }
}
