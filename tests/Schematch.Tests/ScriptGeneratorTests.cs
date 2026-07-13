using Schematch.Core.Compare;
using Schematch.Core.Model;
using Schematch.Core.Providers.PostgreSql;
using Schematch.Core.Providers.SqlServer;
using Schematch.Core.Scripting;
using static Schematch.Tests.ModelBuilder;

namespace Schematch.Tests;

public class SqlServerScriptGeneratorTests
{
    private static string Sync(DatabaseSchema source, DatabaseSchema target, ScriptOptions? options = null)
    {
        var comparison = new SchemaComparer().Compare(source, target);
        var selected = comparison.Differences.Where(d => d.Status != DiffStatus.Equal).ToList();
        return new SqlServerScriptGenerator().Generate(comparison, selected, options ?? new ScriptOptions());
    }

    [Fact]
    public void Missing_table_produces_create_table_with_pk()
    {
        string script = Sync(
            Db("a", Table("Orders", Col("Id", "int", nullable: false, identity: "IDENTITY(1,1)"), Col("Name", "nvarchar(50)")).WithPk("Id")),
            Db("b"));
        Assert.Contains("CREATE TABLE [dbo].[Orders]", script);
        Assert.Contains("[Id] int IDENTITY(1,1) NOT NULL", script);
        Assert.Contains("CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id])", script);
    }

    [Fact]
    public void Foreign_keys_are_created_after_all_tables()
    {
        string script = Sync(
            Db("a",
                Table("Child", Col("Id", "int", nullable: false), Col("ParentId", "int")).WithPk("Id")
                    .WithFk("FK_Child_Parent", "ParentId", "Parent", "Id"),
                Table("Parent", Col("Id", "int", nullable: false)).WithPk("Id")),
            Db("b"));
        int createParent = script.IndexOf("CREATE TABLE [dbo].[Parent]");
        int createFk = script.IndexOf("ADD CONSTRAINT [FK_Child_Parent]");
        Assert.True(createParent >= 0 && createFk > createParent,
            "FK must be added after both tables exist");
        Assert.Contains("REFERENCES [dbo].[Parent] ([Id])", script);
    }

    [Fact]
    public void Changed_column_produces_alter_column_with_warning()
    {
        string script = Sync(
            Db("a", Table("T", Col("Price", "decimal(18,4)", nullable: false))),
            Db("b", Table("T", Col("Price", "decimal(10,2)", nullable: false))));
        Assert.Contains("ALTER TABLE [dbo].[T] ALTER COLUMN [Price] decimal(18,4) NOT NULL;", script);
        Assert.Contains("-- WARNING", script);
    }

    [Fact]
    public void Target_only_table_dropped_only_when_drops_enabled()
    {
        var source = Db("a");
        var target = Db("b", Table("Legacy", Col("Id", "int")));

        Assert.Contains("DROP TABLE [dbo].[Legacy];", Sync(source, target, new ScriptOptions { IncludeDrops = true }));
        Assert.DoesNotContain("DROP TABLE", Sync(source, target, new ScriptOptions { IncludeDrops = false }));
    }

    [Fact]
    public void Changed_procedure_uses_create_or_alter_in_own_batch()
    {
        var source = Db("a");
        source.CodeObjects.Add(Code(CodeObjectKind.Procedure, "P", "CREATE PROCEDURE dbo.P AS SELECT 2"));
        var target = Db("b");
        target.CodeObjects.Add(Code(CodeObjectKind.Procedure, "P", "CREATE PROCEDURE dbo.P AS SELECT 1"));

        string script = Sync(source, target);
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.P AS SELECT 2", script);
        int pos = script.IndexOf("CREATE OR ALTER PROCEDURE");
        Assert.Contains("GO", script[..pos]);
    }

    [Fact]
    public void Changed_index_is_dropped_then_recreated()
    {
        string script = Sync(
            Db("a", Table("T", Col("A", "int")).WithIndex("IX", unique: true, "A")),
            Db("b", Table("T", Col("A", "int")).WithIndex("IX", unique: false, "A")));
        int drop = script.IndexOf("DROP INDEX [IX] ON [dbo].[T];");
        int create = script.IndexOf("CREATE UNIQUE INDEX [IX] ON [dbo].[T] ([A]);");
        Assert.True(drop >= 0 && create > drop, "index must be dropped before recreation");
    }

    [Fact]
    public void New_not_null_column_without_default_gets_warning()
    {
        string script = Sync(
            Db("a", Table("T", Col("Id", "int"), Col("Extra", "int", nullable: false))),
            Db("b", Table("T", Col("Id", "int"))));
        Assert.Contains("ALTER TABLE [dbo].[T] ADD [Extra] int NOT NULL;", script);
        Assert.Contains("WARNING", script);
    }

    [Fact]
    public void Missing_schema_is_created_first()
    {
        var source = Db("a", Table("T", Col("Id", "int")));
        source.Schemas.Add("sales");
        var target = Db("b");

        string script = Sync(source, target);
        Assert.Contains("IF SCHEMA_ID(N'sales') IS NULL EXEC(N'CREATE SCHEMA [sales]');", script);
        Assert.True(script.IndexOf("CREATE SCHEMA") < script.IndexOf("CREATE TABLE"));
    }
}

public class PostgreSqlScriptGeneratorTests
{
    private static string Sync(DatabaseSchema source, DatabaseSchema target, ScriptOptions? options = null)
    {
        var comparison = new SchemaComparer().Compare(source, target);
        var selected = comparison.Differences.Where(d => d.Status != DiffStatus.Equal).ToList();
        return new PostgreSqlScriptGenerator().Generate(comparison, selected, options ?? new ScriptOptions());
    }

    [Fact]
    public void Type_change_uses_alter_type_with_using_cast()
    {
        string script = Sync(
            Db("a", Table("t", Col("price", "numeric(18,4)", nullable: false))),
            Db("b", Table("t", Col("price", "numeric(10,2)", nullable: false))));
        Assert.Contains("ALTER TABLE \"dbo\".\"t\" ALTER COLUMN \"price\" TYPE numeric(18,4) USING \"price\"::numeric(18,4);", script);
    }

    [Fact]
    public void Nullability_change_uses_set_drop_not_null()
    {
        string script = Sync(
            Db("a", Table("t", Col("name", "text", nullable: false))),
            Db("b", Table("t", Col("name", "text", nullable: true))));
        Assert.Contains("ALTER TABLE \"dbo\".\"t\" ALTER COLUMN \"name\" SET NOT NULL;", script);
    }

    [Fact]
    public void Changed_view_is_dropped_before_recreation()
    {
        var source = Db("a");
        source.CodeObjects.Add(Code(CodeObjectKind.View, "v", "CREATE OR REPLACE VIEW \"dbo\".\"v\" AS\nSELECT 2;"));
        var target = Db("b");
        target.CodeObjects.Add(Code(CodeObjectKind.View, "v", "CREATE OR REPLACE VIEW \"dbo\".\"v\" AS\nSELECT 1;"));

        string script = Sync(source, target);
        int drop = script.IndexOf("DROP VIEW IF EXISTS \"dbo\".\"v\";");
        int create = script.IndexOf("CREATE OR REPLACE VIEW \"dbo\".\"v\" AS\nSELECT 2;");
        Assert.True(drop >= 0 && create > drop, "view must be dropped before recreation");
    }

    [Fact]
    public void Function_drop_uses_identity_arguments()
    {
        var source = Db("a");
        var target = Db("b");
        target.CodeObjects.Add(Code(CodeObjectKind.Function, "fn(integer, text)", "CREATE OR REPLACE FUNCTION dbo.fn(a integer, b text) ..."));

        string script = Sync(source, target);
        Assert.Contains("DROP FUNCTION IF EXISTS \"dbo\".\"fn\"(integer, text);", script);
    }

    [Fact]
    public void Trigger_drop_references_parent_table()
    {
        var source = Db("a");
        var target = Db("b");
        target.CodeObjects.Add(Code(CodeObjectKind.Trigger, "trg_audit", "CREATE TRIGGER trg_audit ...", parent: "dbo.orders"));

        string script = Sync(source, target);
        Assert.Contains("DROP TRIGGER IF EXISTS \"trg_audit\" ON \"dbo\".\"orders\";", script);
    }

    [Fact]
    public void Index_recreation_uses_raw_definition()
    {
        var srcTable = Table("t", Col("a", "integer"));
        srcTable.Indexes.Add(new IndexModel
        {
            Name = "ix_t_a",
            IsUnique = true,
            RawDefinition = "CREATE UNIQUE INDEX ix_t_a ON dbo.t USING btree (a)",
        });
        string script = Sync(Db("a", srcTable), Db("b", Table("t", Col("a", "integer"))));
        Assert.Contains("CREATE UNIQUE INDEX ix_t_a ON dbo.t USING btree (a);", script);
    }

    [Fact]
    public void Transaction_wrapper_is_begin_commit()
    {
        string script = Sync(Db("a", Table("t", Col("a", "integer"))), Db("b"));
        Assert.Contains("BEGIN;", script);
        Assert.Contains("COMMIT;", script);
    }
}

public class CodeObjectOrderingTests
{
    [Fact]
    public void Views_referencing_views_are_created_in_dependency_order()
    {
        var inner = Code(CodeObjectKind.View, "v_inner", "CREATE VIEW dbo.v_inner AS SELECT 1 AS x");
        var outer = Code(CodeObjectKind.View, "v_outer", "CREATE VIEW dbo.v_outer AS SELECT x FROM dbo.v_inner");

        var ordered = SyncScriptGeneratorBase.OrderForCreation(new List<CodeObjectModel> { outer, inner }).ToList();
        Assert.Equal("v_inner", ordered[0].Name);
        Assert.Equal("v_outer", ordered[1].Name);
    }

    [Fact]
    public void Functions_come_before_views_and_triggers_last()
    {
        var view = Code(CodeObjectKind.View, "v", "CREATE VIEW v AS SELECT dbo.fn()");
        var fn = Code(CodeObjectKind.Function, "fn", "CREATE FUNCTION fn() ...");
        var trg = Code(CodeObjectKind.Trigger, "trg", "CREATE TRIGGER trg ...");
        var proc = Code(CodeObjectKind.Procedure, "p", "CREATE PROCEDURE p AS SELECT 1");

        var ordered = SyncScriptGeneratorBase.OrderForCreation(new List<CodeObjectModel> { trg, view, proc, fn }).ToList();
        Assert.Equal(new[] { "fn", "v", "p", "trg" }, ordered.Select(o => o.Name).ToArray());
    }
}
