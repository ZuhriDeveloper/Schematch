using Schematch.Core.Model;

namespace Schematch.Core.Compare;

/// <summary>Compares two engine-neutral schema snapshots and classifies every object.</summary>
public sealed class SchemaComparer
{
    private readonly CompareOptions _options;

    public SchemaComparer(CompareOptions? options = null) => _options = options ?? new CompareOptions();

    public SchemaComparisonResult Compare(DatabaseSchema source, DatabaseSchema target)
    {
        var result = new SchemaComparisonResult { Source = source, Target = target, Options = _options };

        CompareTables(source, target, result);
        CompareCodeObjects(source, target, result);

        result.Differences.Sort((a, b) =>
        {
            int c = a.Type.CompareTo(b.Type);
            if (c != 0) return c;
            c = string.Compare(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    private void CompareTables(DatabaseSchema source, DatabaseSchema target, SchemaComparisonResult result)
    {
        var comparer = _options.NameComparer;
        var targetByName = target.Tables.ToDictionary(t => t.FullName, comparer);
        var matchedTargets = new HashSet<string>(comparer);

        foreach (var src in source.Tables)
        {
            if (targetByName.TryGetValue(src.FullName, out var tgt))
            {
                matchedTargets.Add(tgt.FullName);
                var diff = new ObjectDifference
                {
                    Type = SchemaObjectType.Table,
                    Schema = src.Schema,
                    Name = src.Name,
                    SourceTable = src,
                    TargetTable = tgt,
                };
                CompareTablePair(src, tgt, diff);
                diff.Status = diff.Details.Count == 0 ? DiffStatus.Equal : DiffStatus.Different;
                result.Differences.Add(diff);
            }
            else
            {
                result.Differences.Add(new ObjectDifference
                {
                    Type = SchemaObjectType.Table,
                    Schema = src.Schema,
                    Name = src.Name,
                    SourceTable = src,
                    Status = DiffStatus.SourceOnly,
                });
            }
        }

        foreach (var tgt in target.Tables.Where(t => !matchedTargets.Contains(t.FullName)))
        {
            result.Differences.Add(new ObjectDifference
            {
                Type = SchemaObjectType.Table,
                Schema = tgt.Schema,
                Name = tgt.Name,
                TargetTable = tgt,
                Status = DiffStatus.TargetOnly,
            });
        }
    }

    private void CompareTablePair(TableModel src, TableModel tgt, ObjectDifference diff)
    {
        CompareColumns(src, tgt, diff);
        ComparePrimaryKey(src, tgt, diff);
        CompareNamedSet(diff, "Unique constraint", src.UniqueConstraints, tgt.UniqueConstraints,
            u => u.Name, u => u.Signature);
        CompareNamedSet(diff, "Foreign key", src.ForeignKeys, tgt.ForeignKeys,
            f => f.Name, f => f.Signature);
        CompareNamedSet(diff, "Check constraint", src.CheckConstraints, tgt.CheckConstraints,
            c => c.Name, c => c.Signature);
        CompareNamedSet(diff, "Index", src.Indexes, tgt.Indexes,
            i => i.Name, i => i.Signature);
    }

    private void CompareColumns(TableModel src, TableModel tgt, ObjectDifference diff)
    {
        var comparer = _options.NameComparer;
        var tgtCols = tgt.Columns.ToDictionary(c => c.Name, comparer);
        var matched = new HashSet<string>(comparer);

        foreach (var sc in src.Columns)
        {
            if (!tgtCols.TryGetValue(sc.Name, out var tc))
            {
                diff.Details.Add($"Column [{sc.Name}]: missing in target ({sc.DataType})");
                continue;
            }
            matched.Add(tc.Name);
            diff.Details.AddRange(Scripting.ColumnDiff.Describe(sc, tc, _options));
        }

        foreach (var tc in tgt.Columns.Where(c => !matched.Contains(c.Name) &&
                     !src.Columns.Any(s => comparer.Equals(s.Name, c.Name))))
            diff.Details.Add($"Column [{tc.Name}]: only in target ({tc.DataType})");
    }

    private void ComparePrimaryKey(TableModel src, TableModel tgt, ObjectDifference diff)
    {
        if (src.PrimaryKey is null && tgt.PrimaryKey is null) return;
        if (src.PrimaryKey is null)
        {
            diff.Details.Add($"Primary key [{tgt.PrimaryKey!.Name}]: only in target");
            return;
        }
        if (tgt.PrimaryKey is null)
        {
            diff.Details.Add($"Primary key [{src.PrimaryKey.Name}]: missing in target");
            return;
        }
        if (src.PrimaryKey.Signature != tgt.PrimaryKey.Signature)
            diff.Details.Add($"Primary key: ({Cols(tgt.PrimaryKey)}) → ({Cols(src.PrimaryKey)})");

        static string Cols(KeyConstraintModel k) => string.Join(", ", k.Columns);
    }

    /// <summary>Diffs a named collection (FKs, indexes, constraints) by name, then by structural signature.</summary>
    private void CompareNamedSet<T>(ObjectDifference diff, string kind,
        List<T> source, List<T> target, Func<T, string> name, Func<T, string> signature)
    {
        var comparer = _options.NameComparer;
        var tgtByName = target.ToDictionary(name, comparer);
        var matched = new HashSet<string>(comparer);

        foreach (var s in source)
        {
            if (tgtByName.TryGetValue(name(s), out var t))
            {
                matched.Add(name(t));
                if (signature(s) != signature(t))
                    diff.Details.Add($"{kind} [{name(s)}]: definition differs");
            }
            else
            {
                diff.Details.Add($"{kind} [{name(s)}]: missing in target");
            }
        }

        foreach (var t in target.Where(t => !matched.Contains(name(t))))
            diff.Details.Add($"{kind} [{name(t)}]: only in target");
    }

    private void CompareCodeObjects(DatabaseSchema source, DatabaseSchema target, SchemaComparisonResult result)
    {
        var comparer = _options.NameComparer;
        var tgtByKey = target.CodeObjects.ToDictionary(c => Key(c), comparer);
        var matched = new HashSet<string>(comparer);

        foreach (var src in source.CodeObjects)
        {
            var diffType = ToObjectType(src.Kind);
            if (tgtByKey.TryGetValue(Key(src), out var tgt))
            {
                matched.Add(Key(tgt));
                var diff = new ObjectDifference
                {
                    Type = diffType,
                    Schema = src.Schema,
                    Name = src.Name,
                    SourceCode = src,
                    TargetCode = tgt,
                };
                string a = TextNormalizer.NormalizeModule(src.Definition, _options.IgnoreWhitespaceInModules);
                string b = TextNormalizer.NormalizeModule(tgt.Definition, _options.IgnoreWhitespaceInModules);
                if (a != b)
                {
                    diff.Status = DiffStatus.Different;
                    diff.Details.Add("Definition differs");
                }
                result.Differences.Add(diff);
            }
            else
            {
                result.Differences.Add(new ObjectDifference
                {
                    Type = diffType,
                    Schema = src.Schema,
                    Name = src.Name,
                    SourceCode = src,
                    Status = DiffStatus.SourceOnly,
                });
            }
        }

        foreach (var tgt in target.CodeObjects.Where(c => !matched.Contains(Key(c))))
        {
            result.Differences.Add(new ObjectDifference
            {
                Type = ToObjectType(tgt.Kind),
                Schema = tgt.Schema,
                Name = tgt.Name,
                TargetCode = tgt,
                Status = DiffStatus.TargetOnly,
            });
        }

        static string Key(CodeObjectModel c) => $"{c.Kind}:{c.Schema}.{c.Name}";
    }

    private static SchemaObjectType ToObjectType(CodeObjectKind kind) => kind switch
    {
        CodeObjectKind.View => SchemaObjectType.View,
        CodeObjectKind.Procedure => SchemaObjectType.Procedure,
        CodeObjectKind.Function => SchemaObjectType.Function,
        CodeObjectKind.Trigger => SchemaObjectType.Trigger,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
