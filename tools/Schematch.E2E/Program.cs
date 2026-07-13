// Schematch end-to-end verification: provision drifted demo databases, then prove the round-trip —
// compare → generate deployment script → execute → re-compare must show zero differences,
// for both the schema engine and the data engine. Exits non-zero on any failure.
using System.Data.Common;
using Schematch.Core.Compare;
using Schematch.Core.Data;
using Schematch.Core.Providers;
using Schematch.Core.Scripting;

int failures = 0;
string fixtures = args.FirstOrDefault(a => a.StartsWith("--fixtures="))?["--fixtures=".Length..]
                  ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures");
fixtures = Path.GetFullPath(fixtures);
string engine = args.FirstOrDefault(a => a is "mssql" or "pg") ?? "mssql";
bool provisionOnly = args.Contains("--provision-only");

if (engine == "mssql")
{
    var provider = ProviderRegistry.Get("SQL Server");
    var master = new ConnectionInfo { ProviderName = "SQL Server", Host = @"(localdb)\MSSQLLocalDB", Database = "master", UseWindowsAuth = true };
    var source = master.Clone(); source.Database = "SchematchDemoSource";
    var target = master.Clone(); target.Database = "SchematchDemoTarget";

    Step("Provision LocalDB demo databases");
    await Exec(provider, master, "IF DB_ID('SchematchDemoSource') IS NOT NULL BEGIN ALTER DATABASE SchematchDemoSource SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SchematchDemoSource; END");
    await Exec(provider, master, "IF DB_ID('SchematchDemoTarget') IS NOT NULL BEGIN ALTER DATABASE SchematchDemoTarget SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE SchematchDemoTarget; END");
    await Exec(provider, master, "CREATE DATABASE SchematchDemoSource");
    await Exec(provider, master, "CREATE DATABASE SchematchDemoTarget");
    await RunScriptFile(provider, source, Path.Combine(fixtures, "mssql-source.sql"));
    await RunScriptFile(provider, target, Path.Combine(fixtures, "mssql-target.sql"));

    if (provisionOnly) { Console.WriteLine("\nProvisioned (drifted). Skipping round-trip."); return 0; }
    await RoundTrip(provider, source, target);
}
else
{
    string password = args.FirstOrDefault(a => a.StartsWith("--pg-password="))?["--pg-password=".Length..]
                      ?? Environment.GetEnvironmentVariable("SCHEMATCH_PG_PASSWORD") ?? "postgres";
    var provider = ProviderRegistry.Get("PostgreSQL");
    var admin = new ConnectionInfo { ProviderName = "PostgreSQL", Host = "localhost", Port = 5432, Database = "postgres", UseWindowsAuth = false, Username = "postgres", Password = password };
    var source = admin.Clone(); source.Database = "schematch_demo_source";
    var target = admin.Clone(); target.Database = "schematch_demo_target";

    Step("Provision PostgreSQL demo databases");
    await Exec(provider, admin, "DROP DATABASE IF EXISTS schematch_demo_source WITH (FORCE)");
    await Exec(provider, admin, "DROP DATABASE IF EXISTS schematch_demo_target WITH (FORCE)");
    await Exec(provider, admin, "CREATE DATABASE schematch_demo_source");
    await Exec(provider, admin, "CREATE DATABASE schematch_demo_target");
    await RunScriptFile(provider, source, Path.Combine(fixtures, "pg-source.sql"));
    await RunScriptFile(provider, target, Path.Combine(fixtures, "pg-target.sql"));

    await VerifySchemaScope(provider, source, target);
    await RoundTrip(provider, source, target);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "E2E PASSED" : $"E2E FAILED ({failures} check(s) failed)");
return failures == 0 ? 0 : 1;

// PostgreSQL only: prove that scoping a connection to one schema reads that schema and nothing else.
async Task VerifySchemaScope(IDatabaseProvider provider, ConnectionInfo source, ConnectionInfo target)
{
    Step("Schema-scope verification (PostgreSQL)");

    var schemas = await provider.ListSchemasAsync(source);
    Console.WriteLine($"  schemas in source: {string.Join(", ", schemas)}");
    Check("both 'public' and 'sales' schemas exist", schemas.Contains("public") && schemas.Contains("sales"));

    // Scope both sides to 'public' and compare.
    var srcPublic = source.Clone(); srcPublic.Schema = "public";
    var tgtPublic = target.Clone(); tgtPublic.Schema = "public";
    var scoped = await Compare(provider, srcPublic, tgtPublic);

    bool anySales = scoped.Differences.Any(d => d.Schema.Equals("sales", StringComparison.OrdinalIgnoreCase));
    bool anyPublic = scoped.Differences.Any(d => d.Schema.Equals("public", StringComparison.OrdinalIgnoreCase));
    Check("scoped compare excludes the 'sales' schema", !anySales);
    Check("scoped compare still includes the 'public' schema", anyPublic);

    // Full compare (no scope) must include sales — confirming the exclusion was the scope's doing.
    var full = await Compare(provider, source, target);
    Check("unscoped compare includes the 'sales' schema", full.Differences.Any(d => d.Schema.Equals("sales", StringComparison.OrdinalIgnoreCase)));

    // Scope to 'sales' alone.
    var srcSales = source.Clone(); srcSales.Schema = "sales";
    var tgtSales = target.Clone(); tgtSales.Schema = "sales";
    var salesScoped = await Compare(provider, srcSales, tgtSales);
    Check("'sales'-scoped compare excludes 'public'", !salesScoped.Differences.Any(d => d.Schema.Equals("public", StringComparison.OrdinalIgnoreCase)));
    Check("'sales'-scoped compare includes sales.orders", salesScoped.Differences.Any(d => d.FullName.Equals("sales.orders", StringComparison.OrdinalIgnoreCase)));
}

async Task RoundTrip(IDatabaseProvider provider, ConnectionInfo source, ConnectionInfo target)
{
    Step("Initial schema compare");
    var comparison = await Compare(provider, source, target);
    foreach (var d in comparison.Differences.Where(d => d.Status != DiffStatus.Equal))
    {
        Console.WriteLine($"  {d.Status,-11} {d.Type,-9} {d.FullName}");
        foreach (var detail in d.Details.Take(6)) Console.WriteLine($"      - {detail}");
    }
    Check("differences detected", comparison.Differences.Any(d => d.Status != DiffStatus.Equal));
    Check("a source-only table found", comparison.Differences.Any(d => d.Status == DiffStatus.SourceOnly && d.Type == SchemaObjectType.Table));
    Check("a target-only table found", comparison.Differences.Any(d => d.Status == DiffStatus.TargetOnly && d.Type == SchemaObjectType.Table));
    Check("a changed table found", comparison.Differences.Any(d => d.Status == DiffStatus.Different && d.Type == SchemaObjectType.Table));
    Check("a changed view found", comparison.Differences.Any(d => d.Status == DiffStatus.Different && d.Type == SchemaObjectType.View));

    Step("Generate + execute schema deployment script");
    var selected = comparison.Differences.Where(d => d.Status != DiffStatus.Equal).ToList();
    string script = provider.ScriptGenerator.Generate(comparison, selected, new ScriptOptions { IncludeDrops = true });
    string scriptPath = Path.Combine(fixtures, $"generated-{engine}-schema-sync.sql");
    File.WriteAllText(scriptPath, script);
    Console.WriteLine($"  script saved: {scriptPath}");
    await ExecuteBatches(provider, target, script);

    Step("Re-compare schemas (must be identical)");
    var recheck = await Compare(provider, source, target);
    var leftovers = recheck.Differences.Where(d => d.Status != DiffStatus.Equal).ToList();
    foreach (var d in leftovers)
    {
        Console.WriteLine($"  STILL DIFFERENT: {d.Status} {d.Type} {d.FullName}");
        foreach (var detail in d.Details) Console.WriteLine($"      - {detail}");
    }
    Check("schema round-trip leaves zero differences", leftovers.Count == 0);

    Step("Data compare + sync");
    var dataOptions = new DataCompareOptions();
    long totalChanges = 0;
    var dataScripts = new List<string>();
    foreach (var diff in recheck.Differences.Where(d => d.Type == SchemaObjectType.Table && d.SourceTable is not null && d.TargetTable is not null))
    {
        var dataDiff = await DataComparer.CompareTableAsync(provider, source, target, diff.SourceTable!, diff.TargetTable!, dataOptions);
        Console.WriteLine($"  {dataDiff.TableName}: missing={dataDiff.MissingInTarget} extra={dataDiff.ExtraInTarget} different={dataDiff.DifferentRows} equal={dataDiff.EqualRows} {dataDiff.Error}");
        Check($"data compare ran for {dataDiff.TableName}", dataDiff.Error is null);
        totalChanges += dataDiff.MissingInTarget + dataDiff.ExtraInTarget + dataDiff.DifferentRows;
        if (dataDiff.HasChanges) dataScripts.Add(dataDiff.Script);
    }
    Check("data drift detected", totalChanges > 0);

    string dataScript = provider.TransactionStartStatement + "\n" + string.Join("\n", dataScripts) + "\n" + provider.TransactionEndStatement;
    File.WriteAllText(Path.Combine(fixtures, $"generated-{engine}-data-sync.sql"), dataScript);
    await ExecuteBatches(provider, target, dataScript);

    Step("Re-run data compare (must be in sync)");
    long remaining = 0;
    foreach (var diff in recheck.Differences.Where(d => d.Type == SchemaObjectType.Table && d.SourceTable is not null && d.TargetTable is not null))
    {
        var dataDiff = await DataComparer.CompareTableAsync(provider, source, target, diff.SourceTable!, diff.TargetTable!, dataOptions);
        remaining += dataDiff.MissingInTarget + dataDiff.ExtraInTarget + dataDiff.DifferentRows;
        if (dataDiff.HasChanges)
            Console.WriteLine($"  STILL DIFFERENT: {dataDiff.TableName} missing={dataDiff.MissingInTarget} extra={dataDiff.ExtraInTarget} different={dataDiff.DifferentRows}");
    }
    Check("data round-trip leaves zero differences", remaining == 0);
}

async Task<SchemaComparisonResult> Compare(IDatabaseProvider provider, ConnectionInfo source, ConnectionInfo target)
{
    var s = await provider.ReadSchemaAsync(source);
    var t = await provider.ReadSchemaAsync(target);
    return new SchemaComparer(new CompareOptions()).Compare(s, t);
}

async Task ExecuteBatches(IDatabaseProvider provider, ConnectionInfo info, string script)
{
    await using var conn = provider.CreateConnection(info);
    await conn.OpenAsync();
    var batches = provider.SplitBatches(script);
    int n = 0;
    foreach (var batch in batches)
    {
        n++;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (DbException ex)
        {
            Console.WriteLine($"  BATCH {n}/{batches.Count} FAILED: {ex.Message}");
            Console.WriteLine("  ---- failing batch ----");
            Console.WriteLine(batch.Length > 2000 ? batch[..2000] + "…" : batch);
            failures++;
            try
            {
                await using var rb = conn.CreateCommand();
                rb.CommandText = "ROLLBACK";
                await rb.ExecuteNonQueryAsync();
            }
            catch { }
            throw;
        }
    }
    Console.WriteLine($"  executed {batches.Count} batch(es).");
}

async Task Exec(IDatabaseProvider provider, ConnectionInfo info, string sql)
{
    await using var conn = provider.CreateConnection(info);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
}

async Task RunScriptFile(IDatabaseProvider provider, ConnectionInfo info, string path)
{
    Console.WriteLine($"  applying {Path.GetFileName(path)} to {info.Database}");
    await ExecuteBatches(provider, info, File.ReadAllText(path));
}

void Step(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}

void Check(string what, bool ok)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) failures++;
}
