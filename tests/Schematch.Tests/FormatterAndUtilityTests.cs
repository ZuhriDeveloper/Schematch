using Schematch.Core.Compare;
using Schematch.Core.Providers;
using Schematch.Core.Providers.PostgreSql;
using Schematch.Core.Providers.SqlServer;

namespace Schematch.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("((0))", "0")]
    [InlineData("(getdate())", "getdate()")]
    [InlineData("(A)+(B)", "(a)+(b)")]
    [InlineData("  price  >   0 ", "price > 0")]
    public void Expression_normalization(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.NormalizeExpression(input));

    [Fact]
    public void Module_normalization_unifies_line_endings_and_trailing_whitespace()
    {
        string a = TextNormalizer.NormalizeModule("SELECT 1  \r\nFROM t  \r\n", collapseWhitespace: false);
        string b = TextNormalizer.NormalizeModule("SELECT 1\nFROM t", collapseWhitespace: false);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Module_collapse_whitespace_makes_reformatted_code_equal()
    {
        string a = TextNormalizer.NormalizeModule("SELECT   1\n\n  FROM    t", collapseWhitespace: true);
        string b = TextNormalizer.NormalizeModule("SELECT 1 FROM t", collapseWhitespace: true);
        Assert.Equal(a, b);
    }
}

public class SqlServerLiteralFormatterTests
{
    private readonly SqlServerLiteralFormatter _f = new();

    [Fact] public void Null_is_NULL() => Assert.Equal("NULL", _f.Format(DBNull.Value));
    [Fact] public void String_is_escaped_unicode() => Assert.Equal("N'O''Brien'", _f.Format("O'Brien"));
    [Fact] public void Bool_is_bit() => Assert.Equal("1", _f.Format(true));
    [Fact] public void Bytes_are_hex() => Assert.Equal("0x0AFF", _f.Format(new byte[] { 0x0A, 0xFF }));
    [Fact] public void Decimal_is_invariant() => Assert.Equal("1234.56", _f.Format(1234.56m));

    [Fact]
    public void DateTime_without_fraction_is_seconds_precision() =>
        Assert.Equal("'2026-07-13T10:30:00'", _f.Format(new DateTime(2026, 7, 13, 10, 30, 0)));

    [Fact]
    public void DateTime_fraction_is_trimmed() =>
        Assert.Equal("'2026-07-13T10:30:00.5'", _f.Format(new DateTime(2026, 7, 13, 10, 30, 0).AddMilliseconds(500)));
}

public class PostgreSqlLiteralFormatterTests
{
    private readonly PostgreSqlLiteralFormatter _f = new();

    [Fact] public void String_is_escaped() => Assert.Equal("'O''Brien'", _f.Format("O'Brien"));
    [Fact] public void Bool_is_keyword() => Assert.Equal("TRUE", _f.Format(true));
    [Fact] public void Bytes_are_bytea_hex() => Assert.Equal(@"'\x0AFF'", _f.Format(new byte[] { 0x0A, 0xFF }));
    [Fact] public void Utc_timestamp_gets_offset() =>
        Assert.Equal("'2026-07-13 10:30:00.000000+00'", _f.Format(new DateTime(2026, 7, 13, 10, 30, 0, DateTimeKind.Utc)));
}

public class SplitBatchesTests
{
    private readonly SqlServerProvider _mssql = new();

    [Fact]
    public void Splits_on_GO_lines_case_insensitive()
    {
        var batches = _mssql.SplitBatches("SELECT 1\ngo\nSELECT 2\nGO\nSELECT 3");
        Assert.Equal(3, batches.Count);
        Assert.Equal("SELECT 2", batches[1]);
    }

    [Fact]
    public void GO_inside_text_is_not_a_separator()
    {
        var batches = _mssql.SplitBatches("SELECT 'GO' AS x\nSELECT CATEGORY FROM t -- GO fast");
        Assert.Single(batches);
    }

    [Fact]
    public void Comment_only_batches_are_dropped()
    {
        var batches = _mssql.SplitBatches("-- header comment\nGO\nSELECT 1\nGO");
        Assert.Single(batches);
        Assert.Equal("SELECT 1", batches[0]);
    }

    [Fact]
    public void Postgres_is_single_batch()
    {
        var pg = new PostgreSqlProvider();
        Assert.Single(pg.SplitBatches("SELECT 1;\nSELECT 2;"));
    }
}

public class SqlServerTypeFormattingTests
{
    [Theory]
    [InlineData("nvarchar", (short)100, (byte)0, (byte)0, "nvarchar(50)")]
    [InlineData("nvarchar", (short)-1, (byte)0, (byte)0, "nvarchar(MAX)")]
    [InlineData("varchar", (short)25, (byte)0, (byte)0, "varchar(25)")]
    [InlineData("decimal", (short)9, (byte)18, (byte)4, "decimal(18,4)")]
    [InlineData("datetime2", (short)8, (byte)27, (byte)7, "datetime2(7)")]
    [InlineData("int", (short)4, (byte)10, (byte)0, "int")]
    public void Formats_native_types(string name, short maxLen, byte precision, byte scale, string expected) =>
        Assert.Equal(expected, Schematch.Core.Providers.SqlServer.SqlServerSchemaReader.FormatType(name, maxLen, precision, scale));
}
