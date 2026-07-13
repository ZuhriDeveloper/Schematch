using Schematch.Core.Compare;
using Schematch.Core.Model;
using static Schematch.Tests.ModelBuilder;

namespace Schematch.Tests;

public class SchemaComparerTests
{
    private static SchemaComparisonResult Compare(DatabaseSchema source, DatabaseSchema target, CompareOptions? options = null)
        => new SchemaComparer(options).Compare(source, target);

    [Fact]
    public void Table_only_in_source_is_SourceOnly()
    {
        var result = Compare(Db("a", Table("Orders", Col("Id", "int"))), Db("b"));
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffStatus.SourceOnly, diff.Status);
        Assert.Equal("dbo.Orders", diff.FullName);
    }

    [Fact]
    public void Table_only_in_target_is_TargetOnly()
    {
        var result = Compare(Db("a"), Db("b", Table("Legacy", Col("Id", "int"))));
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffStatus.TargetOnly, diff.Status);
    }

    [Fact]
    public void Identical_tables_are_Equal()
    {
        var result = Compare(
            Db("a", Table("T", Col("Id", "int", nullable: false)).WithPk("Id")),
            Db("b", Table("T", Col("Id", "int", nullable: false)).WithPk("Id")));
        Assert.Equal(DiffStatus.Equal, Assert.Single(result.Differences).Status);
    }

    [Fact]
    public void Column_type_change_is_reported()
    {
        var result = Compare(
            Db("a", Table("T", Col("Price", "decimal(18,4)"))),
            Db("b", Table("T", Col("Price", "decimal(10,2)"))));
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffStatus.Different, diff.Status);
        Assert.Contains(diff.Details, d => d.Contains("decimal(10,2)") && d.Contains("decimal(18,4)"));
    }

    [Fact]
    public void Added_and_removed_columns_are_reported()
    {
        var result = Compare(
            Db("a", Table("T", Col("Id", "int"), Col("New", "int"))),
            Db("b", Table("T", Col("Id", "int"), Col("Old", "int"))));
        var diff = Assert.Single(result.Differences);
        Assert.Contains(diff.Details, d => d.Contains("[New]") && d.Contains("missing in target"));
        Assert.Contains(diff.Details, d => d.Contains("[Old]") && d.Contains("only in target"));
    }

    [Fact]
    public void Default_expression_differences_ignore_redundant_parentheses()
    {
        var result = Compare(
            Db("a", Table("T", Col("Id", "int", defaultExpr: "((0))"))),
            Db("b", Table("T", Col("Id", "int", defaultExpr: "0"))));
        Assert.Equal(DiffStatus.Equal, Assert.Single(result.Differences).Status);
    }

    [Fact]
    public void Index_difference_is_reported()
    {
        var result = Compare(
            Db("a", Table("T", Col("Id", "int")).WithIndex("IX_T", unique: false, "Id")),
            Db("b", Table("T", Col("Id", "int"))));
        var diff = Assert.Single(result.Differences);
        Assert.Contains(diff.Details, d => d.Contains("Index [IX_T]") && d.Contains("missing in target"));
    }

    [Fact]
    public void Index_signature_change_is_reported()
    {
        var result = Compare(
            Db("a", Table("T", Col("Id", "int")).WithIndex("IX_T", unique: true, "Id")),
            Db("b", Table("T", Col("Id", "int")).WithIndex("IX_T", unique: false, "Id")));
        var diff = Assert.Single(result.Differences);
        Assert.Contains(diff.Details, d => d.Contains("Index [IX_T]") && d.Contains("definition differs"));
    }

    [Fact]
    public void Module_whitespace_is_ignored_when_option_set()
    {
        var source = Db("a");
        source.CodeObjects.Add(Code(CodeObjectKind.View, "V", "CREATE VIEW dbo.V AS\r\nSELECT  1  AS x"));
        var target = Db("b");
        target.CodeObjects.Add(Code(CodeObjectKind.View, "V", "CREATE VIEW dbo.V AS SELECT 1 AS x"));

        var relaxed = Compare(source, target, new CompareOptions { IgnoreWhitespaceInModules = true });
        Assert.Equal(DiffStatus.Equal, Assert.Single(relaxed.Differences).Status);

        var strict = Compare(source, target, new CompareOptions { IgnoreWhitespaceInModules = false });
        Assert.Equal(DiffStatus.Different, Assert.Single(strict.Differences).Status);
    }

    [Fact]
    public void Case_insensitive_matching_can_be_disabled()
    {
        var source = Db("a", Table("Orders", Col("Id", "int")));
        var target = Db("b", Table("ORDERS", Col("Id", "int")));

        var insensitive = Compare(source, target);
        Assert.Equal(DiffStatus.Equal, Assert.Single(insensitive.Differences).Status);

        var sensitive = Compare(source, target, new CompareOptions { IgnoreCase = false });
        Assert.Equal(2, sensitive.Differences.Count);
        Assert.Contains(sensitive.Differences, d => d.Status == DiffStatus.SourceOnly);
        Assert.Contains(sensitive.Differences, d => d.Status == DiffStatus.TargetOnly);
    }

    [Fact]
    public void Primary_key_change_is_reported()
    {
        var result = Compare(
            Db("a", Table("T", Col("A", "int"), Col("B", "int")).WithPk("A", "B")),
            Db("b", Table("T", Col("A", "int"), Col("B", "int")).WithPk("A")));
        var diff = Assert.Single(result.Differences);
        Assert.Contains(diff.Details, d => d.StartsWith("Primary key"));
    }
}
