namespace Schematch.Core.Model;

public enum CodeObjectKind
{
    View,
    Procedure,
    Function,
    Trigger,
}

/// <summary>A programmable object whose identity is its DDL text: view, procedure, function, or trigger.</summary>
public sealed class CodeObjectModel
{
    public CodeObjectKind Kind { get; set; }
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Full DDL as stored by the engine (CREATE VIEW ... / CREATE FUNCTION ...).</summary>
    public string Definition { get; set; } = "";

    /// <summary>For triggers: "schema.table" the trigger belongs to.</summary>
    public string? ParentTable { get; set; }

    public string FullName => $"{Schema}.{Name}";
}
