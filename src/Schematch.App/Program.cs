using Schematch.App.UI;
using Schematch.Core.Providers;

namespace Schematch.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var main = new MainForm();

        // Verification seam: `Schematch.App --demo-mssql` preloads the LocalDB demo databases
        // and runs the compare automatically, so the full UI can be exercised deterministically.
        // `--demo-mssql-connstr` does the same but via raw connection strings (exercises that path).
        if (args.Contains("--demo-mssql-connstr"))
        {
            var source = new ConnectionInfo { ProviderName = "SQL Server", ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=SchematchDemoSource;Integrated Security=true;TrustServerCertificate=true", Database = "SchematchDemoSource" };
            var target = new ConnectionInfo { ProviderName = "SQL Server", ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=SchematchDemoTarget;Integrated Security=true;TrustServerCertificate=true", Database = "SchematchDemoTarget" };
            main.LoadDemo(source, target);
        }
        else if (args.Contains("--demo-mssql"))
        {
            var source = new ConnectionInfo { ProviderName = "SQL Server", Host = @"(localdb)\MSSQLLocalDB", Database = "SchematchDemoSource", UseWindowsAuth = true };
            var target = new ConnectionInfo { ProviderName = "SQL Server", Host = @"(localdb)\MSSQLLocalDB", Database = "SchematchDemoTarget", UseWindowsAuth = true };
            main.LoadDemo(source, target);
        }

        Application.Run(main);
    }
}
