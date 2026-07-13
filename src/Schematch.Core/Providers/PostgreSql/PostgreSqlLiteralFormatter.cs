using System.Globalization;
using Schematch.Core.Data;

namespace Schematch.Core.Providers.PostgreSql;

public sealed class PostgreSqlLiteralFormatter : ISqlLiteralFormatter
{
    public string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => $"'{s.Replace("'", "''")}'",
        bool b => b ? "TRUE" : "FALSE",
        byte or short or int or long or sbyte or ushort or uint or ulong =>
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        DateTime dt => FormatDateTime(dt),
        DateTimeOffset dto => $"'{dto.ToString("yyyy-MM-dd HH:mm:ss.ffffff zzz", CultureInfo.InvariantCulture)}'",
        TimeSpan ts => $"'{ts:hh\\:mm\\:ss\\.ffffff}'",
        DateOnly d => $"'{d:yyyy-MM-dd}'",
        TimeOnly t => $"'{t:HH\\:mm\\:ss\\.ffffff}'",
        Guid g => $"'{g}'",
        byte[] bytes => $@"'\x{Convert.ToHexString(bytes)}'",
        char c => $"'{(c == '\'' ? "''" : c.ToString())}'",
        _ => $"'{value.ToString()?.Replace("'", "''")}'",
    };

    private static string FormatDateTime(DateTime dt)
    {
        string s = dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return dt.Kind == DateTimeKind.Utc ? $"'{s}+00'" : $"'{s}'";
    }
}
