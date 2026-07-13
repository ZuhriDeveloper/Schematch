namespace Schematch.Core.Model;

/// <summary>Engine-neutral snapshot of one database's schema.</summary>
public sealed class DatabaseSchema
{
    public string DatabaseName { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public List<string> Schemas { get; } = new();
    public List<TableModel> Tables { get; } = new();
    public List<CodeObjectModel> CodeObjects { get; } = new();
}
