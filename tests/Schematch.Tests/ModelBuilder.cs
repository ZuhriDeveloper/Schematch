using Schematch.Core.Model;

namespace Schematch.Tests;

/// <summary>Compact fixture helpers for building schema models in tests.</summary>
internal static class ModelBuilder
{
    public static DatabaseSchema Db(string name, params TableModel[] tables)
    {
        var db = new DatabaseSchema { DatabaseName = name, ProviderName = "SQL Server" };
        db.Schemas.Add("dbo");
        db.Tables.AddRange(tables);
        return db;
    }

    public static TableModel Table(string name, params ColumnModel[] columns)
    {
        var t = new TableModel { Schema = "dbo", Name = name };
        t.Columns.AddRange(columns);
        return t;
    }

    public static ColumnModel Col(string name, string type, bool nullable = true,
        string? defaultExpr = null, string? identity = null, int ordinal = 0)
        => new()
        {
            Name = name,
            DataType = type,
            IsNullable = nullable,
            DefaultExpression = defaultExpr,
            IdentityClause = identity,
            Ordinal = ordinal,
        };

    public static TableModel WithPk(this TableModel t, params string[] columns)
    {
        var pk = new KeyConstraintModel { Name = $"PK_{t.Name}", IsPrimaryKey = true, IsClustered = true };
        pk.Columns.AddRange(columns.Select(c => new IndexColumn { Name = c }));
        t.PrimaryKey = pk;
        return t;
    }

    public static TableModel WithIndex(this TableModel t, string name, bool unique, params string[] columns)
    {
        var ix = new IndexModel { Name = name, IsUnique = unique };
        ix.Columns.AddRange(columns.Select(c => new IndexColumn { Name = c }));
        t.Indexes.Add(ix);
        return t;
    }

    public static TableModel WithFk(this TableModel t, string name, string column, string refTable, string refColumn)
    {
        var fk = new ForeignKeyModel { Name = name, ReferencedSchema = "dbo", ReferencedTable = refTable };
        fk.Columns.Add(column);
        fk.ReferencedColumns.Add(refColumn);
        t.ForeignKeys.Add(fk);
        return t;
    }

    public static TableModel WithCheck(this TableModel t, string name, string expression)
    {
        t.CheckConstraints.Add(new CheckConstraintModel { Name = name, Expression = expression });
        return t;
    }

    public static CodeObjectModel Code(CodeObjectKind kind, string name, string definition, string? parent = null)
        => new() { Kind = kind, Schema = "dbo", Name = name, Definition = definition, ParentTable = parent };
}
