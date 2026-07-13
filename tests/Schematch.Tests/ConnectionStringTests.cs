using Schematch.Core.Providers;
using Schematch.Core.Providers.PostgreSql;
using Schematch.Core.Providers.SqlServer;

namespace Schematch.Tests;

public class ConnectionStringTests
{
    [Fact]
    public void SqlServer_raw_string_is_used_verbatim_with_database_extracted()
    {
        var provider = new SqlServerProvider();
        var info = new ConnectionInfo
        {
            ProviderName = "SQL Server",
            ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=MyDb;Integrated Security=true",
        };
        Assert.True(info.UsesRawConnectionString);
        Assert.Equal("MyDb", provider.ExtractDatabaseName(info.ConnectionString));

        string built = provider.BuildConnectionString(info);
        Assert.Contains("Initial Catalog=MyDb", built);
        Assert.Contains("Application Name=Schematch", built);
    }

    [Fact]
    public void SqlServer_raw_string_database_override_swaps_catalog()
    {
        var provider = new SqlServerProvider();
        var info = new ConnectionInfo
        {
            ProviderName = "SQL Server",
            ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=MyDb;Integrated Security=true",
        };
        // ListDatabasesAsync connects to master by overriding the catalog.
        string master = provider.BuildConnectionString(info, "master");
        Assert.Contains("Initial Catalog=master", master);
        Assert.DoesNotContain("Initial Catalog=MyDb", master);
    }

    [Fact]
    public void Postgres_raw_string_is_used_with_database_extracted_and_overridable()
    {
        var provider = new PostgreSqlProvider();
        var info = new ConnectionInfo
        {
            ProviderName = "PostgreSQL",
            ConnectionString = "Host=localhost;Port=5432;Database=mydb;Username=u;Password=p",
        };
        Assert.Equal("mydb", provider.ExtractDatabaseName(info.ConnectionString));
        Assert.Contains("Database=mydb", provider.BuildConnectionString(info));
        Assert.Contains("Database=postgres", provider.BuildConnectionString(info, "postgres"));
    }

    [Fact]
    public void Structured_mode_ignores_null_connection_string()
    {
        var info = new ConnectionInfo { ProviderName = "SQL Server", Host = "srv", Database = "db" };
        Assert.False(info.UsesRawConnectionString);
        string built = new SqlServerProvider().BuildConnectionString(info);
        Assert.Contains("Data Source=srv", built);
        Assert.Contains("Initial Catalog=db", built);
    }

    [Fact]
    public void ExtractDatabaseName_returns_empty_for_garbage()
    {
        Assert.Equal("", new SqlServerProvider().ExtractDatabaseName("this is not a=valid;;connection"));
        Assert.Equal("", new PostgreSqlProvider().ExtractDatabaseName("@@@not valid@@@"));
    }

    [Fact]
    public void DisplayName_reflects_raw_mode()
    {
        var raw = new ConnectionInfo { ProviderName = "SQL Server", ConnectionString = "Server=x;Database=Sales", Database = "Sales" };
        Assert.Contains("Sales", raw.DisplayName);
        Assert.Contains("connection string", raw.DisplayName);
    }

    [Fact]
    public void DisplayName_shows_schema_scope_when_set()
    {
        var scoped = new ConnectionInfo { ProviderName = "PostgreSQL", Host = "localhost", Database = "mydb", Schema = "sales" };
        Assert.Contains("mydb", scoped.DisplayName);
        Assert.Contains("[sales]", scoped.DisplayName);

        var whole = new ConnectionInfo { ProviderName = "PostgreSQL", Host = "localhost", Database = "mydb" };
        Assert.DoesNotContain("[", whole.DisplayName);
    }
}
