using Schematch.Core.Compare;
using Schematch.Core.Model;

namespace Schematch.Core.Scripting;

/// <summary>
/// Structured difference between two matched tables, used by script generators.
/// "ToDrop" lists hold the target's models (they carry the constraint names that exist on the target);
/// "ToAdd" lists hold the source's models. An object that changed appears in both lists.
/// </summary>
public sealed class TableDelta
{
    public required TableModel Source { get; init; }
    public required TableModel Target { get; init; }

    public List<ColumnModel> AddedColumns { get; } = new();
    public List<ColumnModel> DroppedColumns { get; } = new();
    public List<(ColumnModel Source, ColumnModel Target)> ChangedColumns { get; } = new();

    public KeyConstraintModel? PrimaryKeyToDrop { get; set; }
    public KeyConstraintModel? PrimaryKeyToAdd { get; set; }
    public List<KeyConstraintModel> UniqueToDrop { get; } = new();
    public List<KeyConstraintModel> UniqueToAdd { get; } = new();
    public List<ForeignKeyModel> ForeignKeysToDrop { get; } = new();
    public List<ForeignKeyModel> ForeignKeysToAdd { get; } = new();
    public List<CheckConstraintModel> ChecksToDrop { get; } = new();
    public List<CheckConstraintModel> ChecksToAdd { get; } = new();
    public List<IndexModel> IndexesToDrop { get; } = new();
    public List<IndexModel> IndexesToAdd { get; } = new();

    public static TableDelta Analyze(TableModel source, TableModel target, CompareOptions options)
    {
        var delta = new TableDelta { Source = source, Target = target };
        var comparer = options.NameComparer;

        var tgtCols = target.Columns.ToDictionary(c => c.Name, comparer);
        var matchedCols = new HashSet<string>(comparer);
        foreach (var sc in source.Columns)
        {
            if (tgtCols.TryGetValue(sc.Name, out var tc))
            {
                matchedCols.Add(tc.Name);
                if (ColumnDiff.Describe(sc, tc, options).Count > 0)
                    delta.ChangedColumns.Add((sc, tc));
            }
            else
            {
                delta.AddedColumns.Add(sc);
            }
        }
        delta.DroppedColumns.AddRange(target.Columns.Where(c => !matchedCols.Contains(c.Name)));

        var pkEqual = source.PrimaryKey?.Signature == target.PrimaryKey?.Signature;
        if (!pkEqual)
        {
            delta.PrimaryKeyToDrop = target.PrimaryKey;
            delta.PrimaryKeyToAdd = source.PrimaryKey;
        }

        DiffNamed(source.UniqueConstraints, target.UniqueConstraints, comparer,
            k => k.Name, k => k.Signature, delta.UniqueToAdd, delta.UniqueToDrop);
        DiffNamed(source.ForeignKeys, target.ForeignKeys, comparer,
            f => f.Name, f => f.Signature, delta.ForeignKeysToAdd, delta.ForeignKeysToDrop);
        DiffNamed(source.CheckConstraints, target.CheckConstraints, comparer,
            c => c.Name, c => c.Signature, delta.ChecksToAdd, delta.ChecksToDrop);
        DiffNamed(source.Indexes, target.Indexes, comparer,
            i => i.Name, i => i.Signature, delta.IndexesToAdd, delta.IndexesToDrop);

        return delta;
    }

    private static void DiffNamed<T>(List<T> source, List<T> target, StringComparer comparer,
        Func<T, string> name, Func<T, string> signature, List<T> toAdd, List<T> toDrop)
    {
        var tgtByName = target.ToDictionary(name, comparer);
        var matched = new HashSet<string>(comparer);
        foreach (var s in source)
        {
            if (tgtByName.TryGetValue(name(s), out var t))
            {
                matched.Add(name(t));
                if (signature(s) != signature(t))
                {
                    toDrop.Add(t);
                    toAdd.Add(s);
                }
            }
            else
            {
                toAdd.Add(s);
            }
        }
        toDrop.AddRange(target.Where(t => !matched.Contains(name(t))));
    }
}

/// <summary>Single source of truth for "are these two columns equal" — used by the comparer (display) and TableDelta (scripting).</summary>
public static class ColumnDiff
{
    public static List<string> Describe(ColumnModel sc, ColumnModel tc, CompareOptions options)
    {
        var details = new List<string>();
        if (!string.Equals(sc.DataType, tc.DataType, StringComparison.OrdinalIgnoreCase))
            details.Add($"Column [{sc.Name}]: type {tc.DataType} → {sc.DataType}");
        if (sc.IsNullable != tc.IsNullable)
            details.Add($"Column [{sc.Name}]: {(tc.IsNullable ? "NULL" : "NOT NULL")} → {(sc.IsNullable ? "NULL" : "NOT NULL")}");
        if (TextNormalizer.NormalizeExpression(sc.DefaultExpression) != TextNormalizer.NormalizeExpression(tc.DefaultExpression))
            details.Add($"Column [{sc.Name}]: default {Show(tc.DefaultExpression)} → {Show(sc.DefaultExpression)}");
        if (TextNormalizer.NormalizeExpression(sc.IdentityClause) != TextNormalizer.NormalizeExpression(tc.IdentityClause))
            details.Add($"Column [{sc.Name}]: identity {Show(tc.IdentityClause)} → {Show(sc.IdentityClause)}");
        if (TextNormalizer.NormalizeExpression(sc.ComputedExpression) != TextNormalizer.NormalizeExpression(tc.ComputedExpression)
            || (sc.ComputedExpression is not null && sc.IsPersisted != tc.IsPersisted))
            details.Add($"Column [{sc.Name}]: computed {Show(tc.ComputedExpression)} → {Show(sc.ComputedExpression)}");
        if (!options.IgnoreCollation &&
            !string.Equals(sc.Collation ?? "", tc.Collation ?? "", StringComparison.OrdinalIgnoreCase))
            details.Add($"Column [{sc.Name}]: collation {Show(tc.Collation)} → {Show(sc.Collation)}");
        return details;

        static string Show(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;
    }
}
