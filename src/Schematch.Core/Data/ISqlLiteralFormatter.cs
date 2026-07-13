namespace Schematch.Core.Data;

/// <summary>Renders CLR values from a DbDataReader as SQL literals in the provider's dialect.</summary>
public interface ISqlLiteralFormatter
{
    string Format(object? value);
}
