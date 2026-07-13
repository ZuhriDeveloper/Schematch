namespace Schematch.Core.Compare;

public sealed class CompareOptions
{
    /// <summary>Match object/column names case-insensitively (default: true).</summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>Collapse all whitespace when comparing view/proc/function/trigger definitions.</summary>
    public bool IgnoreWhitespaceInModules { get; set; } = true;

    /// <summary>Ignore column collation differences (common noise across servers with different defaults).</summary>
    public bool IgnoreCollation { get; set; } = true;

    public StringComparer NameComparer =>
        IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public StringComparison NameComparison =>
        IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
