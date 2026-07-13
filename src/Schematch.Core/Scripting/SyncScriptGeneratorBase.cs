using System.Text;
using Schematch.Core.Compare;
using Schematch.Core.Model;

namespace Schematch.Core.Scripting;

/// <summary>
/// Shared deployment-script orchestration. Emits phases in dependency-safe order;
/// derived classes supply the dialect for each statement kind.
/// Direction is always: make the TARGET match the SOURCE.
/// </summary>
public abstract class SyncScriptGeneratorBase : ISyncScriptGenerator
{
    public string Generate(SchemaComparisonResult comparison, IReadOnlyList<ObjectDifference> selected, ScriptOptions options)
    {
        var sb = new StringBuilder();
        var opts = comparison.Options;

        var tableDiffs = selected.Where(d => d.Type == SchemaObjectType.Table).ToList();
        var codeDiffs = selected.Where(d => d.Type != SchemaObjectType.Table).ToList();

        var deltas = tableDiffs
            .Where(d => d.Status == DiffStatus.Different)
            .Select(d => TableDelta.Analyze(d.SourceTable!, d.TargetTable!, opts))
            .ToList();
        var newTables = tableDiffs.Where(d => d.Status == DiffStatus.SourceOnly).Select(d => d.SourceTable!).ToList();
        var droppedTables = options.IncludeDrops
            ? tableDiffs.Where(d => d.Status == DiffStatus.TargetOnly).Select(d => d.TargetTable!).ToList()
            : new List<TableModel>();

        EmitHeader(sb, comparison);
        EmitTransactionStart(sb, options);

        // Phase 0: schemas the source uses that the target lacks.
        var missingSchemas = comparison.Source.Schemas
            .Where(s => !comparison.Target.Schemas.Contains(s, opts.NameComparer))
            .ToList();
        if (missingSchemas.Count > 0)
        {
            Section(sb, "Create missing schemas");
            foreach (var schema in missingSchemas)
                EmitCreateSchema(sb, schema);
        }

        // Phase 1: drop code objects that will be removed or recreated.
        var codeToDrop = codeDiffs
            .Where(d => (d.Status == DiffStatus.TargetOnly && options.IncludeDrops)
                        || (d.Status == DiffStatus.Different && NeedsDropBeforeCreate(d.SourceCode!, d.TargetCode!)))
            .Select(d => d.TargetCode!)
            .ToList();
        if (codeToDrop.Count > 0)
        {
            Section(sb, "Drop removed/recreated code objects");
            // Reverse creation order: triggers → procedures → views (reverse-topo) → functions.
            foreach (var code in OrderForCreation(codeToDrop).Reverse())
                EmitDropCodeObject(sb, code);
        }

        // Phase 2: drop foreign keys — changed/removed FKs, plus every target FK that
        // references a table being dropped (from any table, dropped or kept).
        var fkDrops = new List<(TableModel Table, ForeignKeyModel Fk)>();
        foreach (var delta in deltas)
            fkDrops.AddRange(delta.ForeignKeysToDrop.Select(fk => (delta.Target, fk)));
        if (droppedTables.Count > 0)
        {
            var droppedNames = new HashSet<string>(droppedTables.Select(t => t.FullName), opts.NameComparer);
            foreach (var table in comparison.Target.Tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    bool alreadyListed = fkDrops.Any(x => opts.NameComparer.Equals(x.Table.FullName, table.FullName)
                                                          && opts.NameComparer.Equals(x.Fk.Name, fk.Name));
                    bool referencesDropped = droppedNames.Contains($"{fk.ReferencedSchema}.{fk.ReferencedTable}");
                    bool ownTableDropped = droppedNames.Contains(table.FullName);
                    if (!alreadyListed && (referencesDropped || ownTableDropped))
                        fkDrops.Add((table, fk));
                }
            }
        }
        if (fkDrops.Count > 0)
        {
            Section(sb, "Drop foreign keys");
            foreach (var (table, fk) in fkDrops)
                EmitDropForeignKey(sb, table, fk);
        }

        // Phase 3: drop changed/removed indexes and constraints on kept tables.
        if (deltas.Any(d => d.IndexesToDrop.Count > 0 || d.UniqueToDrop.Count > 0
                            || d.ChecksToDrop.Count > 0 || d.PrimaryKeyToDrop is not null))
        {
            Section(sb, "Drop changed/removed indexes and constraints");
            foreach (var delta in deltas)
            {
                foreach (var ix in delta.IndexesToDrop) EmitDropIndex(sb, delta.Target, ix);
                foreach (var uq in delta.UniqueToDrop) EmitDropConstraint(sb, delta.Target, uq.Name);
                foreach (var ck in delta.ChecksToDrop) EmitDropConstraint(sb, delta.Target, ck.Name);
                if (delta.PrimaryKeyToDrop is not null) EmitDropConstraint(sb, delta.Target, delta.PrimaryKeyToDrop.Name);
            }
        }

        // Phase 4: drop target-only tables.
        if (droppedTables.Count > 0)
        {
            Section(sb, "Drop tables that exist only in the target");
            foreach (var table in droppedTables)
            {
                sb.AppendLine($"-- WARNING: dropping table {table.FullName} discards its data.");
                EmitDropTable(sb, table);
            }
        }

        // Phase 5: create missing tables (FKs deferred to phase 9).
        if (newTables.Count > 0)
        {
            Section(sb, "Create missing tables");
            foreach (var table in newTables)
            {
                sb.AppendLine(ScriptCreateTable(table));
            }
        }

        // Phase 6: alter kept tables (add/alter/drop columns).
        if (deltas.Any(d => d.AddedColumns.Count > 0 || d.ChangedColumns.Count > 0
                            || (options.IncludeDrops && d.DroppedColumns.Count > 0)))
        {
            Section(sb, "Alter columns");
            foreach (var delta in deltas)
                EmitAlterColumns(sb, delta, options);
        }

        // Phase 7: (re)create PKs, unique and check constraints.
        if (deltas.Any(d => d.PrimaryKeyToAdd is not null || d.UniqueToAdd.Count > 0 || d.ChecksToAdd.Count > 0))
        {
            Section(sb, "Create primary keys and constraints");
            foreach (var delta in deltas)
            {
                if (delta.PrimaryKeyToAdd is not null) EmitAddKeyConstraint(sb, delta.Source, delta.PrimaryKeyToAdd);
                foreach (var uq in delta.UniqueToAdd) EmitAddKeyConstraint(sb, delta.Source, uq);
                foreach (var ck in delta.ChecksToAdd) EmitAddCheckConstraint(sb, delta.Source, ck);
            }
        }

        // Phase 8: create indexes (new tables' indexes too — CREATE TABLE emits only columns/PK/constraints).
        var indexCreates = new List<(TableModel Table, IndexModel Index)>();
        foreach (var delta in deltas)
            indexCreates.AddRange(delta.IndexesToAdd.Select(ix => (delta.Source, ix)));
        foreach (var table in newTables)
            indexCreates.AddRange(table.Indexes.Select(ix => (table, ix)));
        if (indexCreates.Count > 0)
        {
            Section(sb, "Create indexes");
            foreach (var (table, ix) in indexCreates)
                EmitCreateIndex(sb, table, ix);
        }

        // Phase 9: create foreign keys (changed/missing on kept tables + all FKs of new tables).
        var fkCreates = new List<(TableModel Table, ForeignKeyModel Fk)>();
        foreach (var delta in deltas)
            fkCreates.AddRange(delta.ForeignKeysToAdd.Select(fk => (delta.Source, fk)));
        foreach (var table in newTables)
            fkCreates.AddRange(table.ForeignKeys.Select(fk => (table, fk)));
        if (fkCreates.Count > 0)
        {
            Section(sb, "Create foreign keys");
            foreach (var (table, fk) in fkCreates)
                EmitAddForeignKey(sb, table, fk);
        }

        // Phase 10: create/replace code objects — functions, then views (topologically
        // sorted, views may reference views), then procedures, then triggers.
        var codeToCreate = codeDiffs
            .Where(d => d.Status is DiffStatus.SourceOnly or DiffStatus.Different)
            .Select(d => d.SourceCode!)
            .ToList();
        if (codeToCreate.Count > 0)
        {
            Section(sb, "Create/replace code objects");
            foreach (var code in OrderForCreation(codeToCreate))
                EmitCreateOrReplaceCodeObject(sb, code);
        }

        EmitTransactionEnd(sb, options);
        return sb.ToString();
    }

    /// <summary>Functions → views → procedures → triggers; within a kind, dependency-ordered by definition text scan.</summary>
    internal static IEnumerable<CodeObjectModel> OrderForCreation(List<CodeObjectModel> objects)
    {
        foreach (var kind in new[] { CodeObjectKind.Function, CodeObjectKind.View, CodeObjectKind.Procedure, CodeObjectKind.Trigger })
        {
            var group = objects.Where(o => o.Kind == kind).ToList();
            foreach (var obj in TopoSortByReference(group))
                yield return obj;
        }
    }

    /// <summary>
    /// Cheap dependency ordering: if A's definition mentions B's name, create B first.
    /// Not a real parser — cycles or false positives just fall back to stable order.
    /// </summary>
    private static List<CodeObjectModel> TopoSortByReference(List<CodeObjectModel> group)
    {
        var ordered = new List<CodeObjectModel>();
        var remaining = new List<CodeObjectModel>(group);
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(candidate =>
                !remaining.Any(other => !ReferenceEquals(other, candidate) &&
                    candidate.Definition.Contains(other.Name, StringComparison.OrdinalIgnoreCase)));
            next ??= remaining[0]; // cycle — give up on ordering, emit as-is
            ordered.Add(next);
            remaining.Remove(next);
        }
        return ordered;
    }

    protected static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine($"-- ==== {title} ====");
    }

    public abstract string ScriptCreateTable(TableModel table);

    protected abstract void EmitHeader(StringBuilder sb, SchemaComparisonResult comparison);
    protected abstract void EmitTransactionStart(StringBuilder sb, ScriptOptions options);
    protected abstract void EmitTransactionEnd(StringBuilder sb, ScriptOptions options);
    protected abstract void EmitCreateSchema(StringBuilder sb, string schema);

    /// <summary>Whether a changed code object must be dropped before re-creation (vs. CREATE OR ALTER/REPLACE).</summary>
    protected abstract bool NeedsDropBeforeCreate(CodeObjectModel source, CodeObjectModel target);
    protected abstract void EmitDropCodeObject(StringBuilder sb, CodeObjectModel code);
    protected abstract void EmitCreateOrReplaceCodeObject(StringBuilder sb, CodeObjectModel code);

    protected abstract void EmitDropForeignKey(StringBuilder sb, TableModel table, ForeignKeyModel fk);
    protected abstract void EmitAddForeignKey(StringBuilder sb, TableModel table, ForeignKeyModel fk);
    protected abstract void EmitDropConstraint(StringBuilder sb, TableModel table, string constraintName);
    protected abstract void EmitAddKeyConstraint(StringBuilder sb, TableModel table, KeyConstraintModel key);
    protected abstract void EmitAddCheckConstraint(StringBuilder sb, TableModel table, CheckConstraintModel check);
    protected abstract void EmitDropIndex(StringBuilder sb, TableModel table, IndexModel index);
    protected abstract void EmitCreateIndex(StringBuilder sb, TableModel table, IndexModel index);
    protected abstract void EmitDropTable(StringBuilder sb, TableModel table);
    protected abstract void EmitAlterColumns(StringBuilder sb, TableDelta delta, ScriptOptions options);
}
