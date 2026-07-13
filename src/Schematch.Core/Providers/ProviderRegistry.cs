using Schematch.Core.Providers.PostgreSql;
using Schematch.Core.Providers.SqlServer;

namespace Schematch.Core.Providers;

public static class ProviderRegistry
{
    public static IReadOnlyList<IDatabaseProvider> All { get; } = new IDatabaseProvider[]
    {
        new SqlServerProvider(),
        new PostgreSqlProvider(),
    };

    public static IDatabaseProvider Get(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown provider '{name}'.");
}
