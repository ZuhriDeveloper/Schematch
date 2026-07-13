using Schematch.Core.Model;

namespace Schematch.Core.Compare;

public enum DiffStatus
{
    Equal,
    Different,
    /// <summary>Exists only in the source database — sync will create it on the target.</summary>
    SourceOnly,
    /// <summary>Exists only in the target database — sync can drop it.</summary>
    TargetOnly,
}

public enum SchemaObjectType
{
    Table,
    View,
    Procedure,
    Function,
    Trigger,
}

public sealed class ObjectDifference
{
    public SchemaObjectType Type { get; init; }
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public DiffStatus Status { get; set; }

    /// <summary>Human-readable sub-differences, e.g. "Column [Price]: type decimal(10,2) → decimal(18,4)".</summary>
    public List<string> Details { get; } = new();

    public TableModel? SourceTable { get; init; }
    public TableModel? TargetTable { get; init; }
    public CodeObjectModel? SourceCode { get; init; }
    public CodeObjectModel? TargetCode { get; init; }

    public string FullName => $"{Schema}.{Name}";
}

public sealed class SchemaComparisonResult
{
    public required DatabaseSchema Source { get; init; }
    public required DatabaseSchema Target { get; init; }
    public required CompareOptions Options { get; init; }
    public List<ObjectDifference> Differences { get; } = new();
}
