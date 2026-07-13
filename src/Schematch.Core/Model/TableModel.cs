namespace Schematch.Core.Model;

public sealed class TableModel
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ColumnModel> Columns { get; } = new();
    public KeyConstraintModel? PrimaryKey { get; set; }
    public List<KeyConstraintModel> UniqueConstraints { get; } = new();
    public List<ForeignKeyModel> ForeignKeys { get; } = new();
    public List<IndexModel> Indexes { get; } = new();
    public List<CheckConstraintModel> CheckConstraints { get; } = new();

    public string FullName => $"{Schema}.{Name}";
}

public sealed class IndexColumn
{
    public string Name { get; set; } = "";
    public bool IsDescending { get; set; }

    public override string ToString() => IsDescending ? Name + " DESC" : Name;
}

/// <summary>Primary key or unique constraint.</summary>
public sealed class KeyConstraintModel
{
    public string Name { get; set; } = "";
    public bool IsPrimaryKey { get; set; }
    public bool IsClustered { get; set; }
    public List<IndexColumn> Columns { get; } = new();

    /// <summary>Structural identity used by the diff engine (columns + kind, not the name).</summary>
    public string Signature =>
        (IsPrimaryKey ? "PK" : "UQ") + (IsClustered ? ":C:" : ":N:") +
        string.Join(",", Columns.Select(c => c.ToString().ToLowerInvariant()));
}

public sealed class ForeignKeyModel
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; } = new();
    public string ReferencedSchema { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public List<string> ReferencedColumns { get; } = new();
    public string OnDelete { get; set; } = "NO ACTION";
    public string OnUpdate { get; set; } = "NO ACTION";

    public string Signature =>
        $"{string.Join(",", Columns)}->{ReferencedSchema}.{ReferencedTable}({string.Join(",", ReferencedColumns)}) D:{OnDelete} U:{OnUpdate}"
            .ToLowerInvariant();
}

public sealed class IndexModel
{
    public string Name { get; set; } = "";
    public bool IsUnique { get; set; }
    public bool IsClustered { get; set; }
    public List<IndexColumn> Columns { get; } = new();
    public List<string> IncludedColumns { get; } = new();
    public string? FilterExpression { get; set; }

    /// <summary>Full CREATE INDEX statement when the engine provides one (PostgreSQL pg_get_indexdef).</summary>
    public string? RawDefinition { get; set; }

    public string Signature => RawDefinition is not null
        ? Compare.TextNormalizer.NormalizeExpression(RawDefinition)
        : ((IsUnique ? "U" : "") + (IsClustered ? "C" : "") + ":" +
           string.Join(",", Columns.Select(c => c.ToString())) + "|inc:" +
           string.Join(",", IncludedColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)) + "|f:" +
           (FilterExpression ?? "")).ToLowerInvariant();
}

public sealed class CheckConstraintModel
{
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";

    public string Signature => Compare.TextNormalizer.NormalizeExpression(Expression);
}
