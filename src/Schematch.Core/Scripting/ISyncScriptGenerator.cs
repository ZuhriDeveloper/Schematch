using Schematch.Core.Compare;
using Schematch.Core.Model;

namespace Schematch.Core.Scripting;

public sealed class ScriptOptions
{
    /// <summary>Drop objects that exist only in the target.</summary>
    public bool IncludeDrops { get; set; } = true;

    /// <summary>Wrap the script in a transaction.</summary>
    public bool UseTransaction { get; set; } = true;
}

public interface ISyncScriptGenerator
{
    /// <summary>Builds the deployment script that makes the target match the source for the selected differences.</summary>
    string Generate(SchemaComparisonResult comparison, IReadOnlyList<ObjectDifference> selected, ScriptOptions options);

    /// <summary>Full CREATE TABLE statement — also used for the side-by-side DDL display.</summary>
    string ScriptCreateTable(TableModel table);
}
