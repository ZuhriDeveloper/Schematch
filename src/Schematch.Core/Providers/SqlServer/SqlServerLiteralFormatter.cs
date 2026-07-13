using System.Globalization;
using Schematch.Core.Data;

namespace Schematch.Core.Providers.SqlServer;

public sealed class SqlServerLiteralFormatter : ISqlLiteralFormatter
{
    public string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => $"N'{s.Replace("'", "''")}'",
        bool b => b ? "1" : "0",
        byte or short or int or long or sbyte or ushort or uint or ulong =>
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        DateTime dt => $"'{FormatDateTime(dt)}'",
        DateTimeOffset dto => $"'{dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)}'",
        TimeSpan ts => $"'{ts:hh\\:mm\\:ss\\.fffffff}'",
        DateOnly d => $"'{d:yyyy-MM-dd}'",
        TimeOnly t => $"'{t:HH\\:mm\\:ss\\.fffffff}'",
        Guid g => $"'{g}'",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        char c => $"N'{(c == '\'' ? "''" : c.ToString())}'",
        _ => $"N'{value.ToString()?.Replace("'", "''")}'",
    };

    // Keep fractional seconds short: legacy datetime rejects literals with 7 fraction digits.
    private static string FormatDateTime(DateTime dt)
    {
        if (dt.Ticks % TimeSpan.TicksPerSecond == 0)
            return dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        string s = dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }
}
