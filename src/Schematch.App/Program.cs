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
        if (args.Contains("--demo-mssql"))
        {
            var source = new ConnectionInfo { ProviderName = "SQL Server", Host = @"(localdb)\MSSQLLocalDB", Database = "SchematchDemoSource", UseWindowsAuth = true };
            var target = new ConnectionInfo { ProviderName = "SQL Server", Host = @"(localdb)\MSSQLLocalDB", Database = "SchematchDemoTarget", UseWindowsAuth = true };
            main.LoadDemo(source, target);
        }

        Application.Run(main);
    }
}
