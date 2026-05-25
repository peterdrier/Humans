using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0025 — a DbSet table must be referenced by exactly one repository.
/// </summary>
/// <remarks>
/// <para>
/// Enforces two of Peter's hard rules at once (<c>peters-hard-rules.md</c>):
/// "only the repository writes its section's tables" and "a table must only
/// exist in one repository." Ownership is <b>emergent</b>, not declared: the
/// single repository that references a <c>HumansDbContext</c> <c>DbSet</c> is
/// its owner. When more than one repository references the same DbSet, there is
/// no single owner — the table is shared across the repository (and usually
/// section) boundary, which bypasses the owning section's service and its §15
/// cache. Reads count as well as writes: a foreign read bypasses the owner's
/// cache and observes stale state, so it is a coherence bug, not soft coupling.
/// </para>
/// <para>
/// Mechanism: across the <c>Humans.Infrastructure</c> compilation, collect every
/// reference (read or write) to a <c>HumansDbContext</c> DbSet from inside a type
/// implementing <see cref="Humans.Application.Interfaces.Repositories.IRepository"/>,
/// then build <c>DbSet → {referencing repository types}</c>. A table referenced by
/// N&gt;1 repositories produces a diagnostic at each access site. Phase 1 detects
/// only direct references: <c>ctx.&lt;DbSet&gt;</c> property access (including DbSets
/// inherited from <c>IdentityDbContext</c>, e.g. <c>Users</c>) and
/// <c>ctx.Set&lt;TEntity&gt;()</c>.
/// </para>
/// <para>
/// Existing shared tables carry
/// <c>[Grandfathered("HUM0025", …, scope: "&lt;DbSet&gt;")]</c> on each participating
/// repository — one entry per shared table (the attribute allows multiples). The
/// diagnostic downgrades to a warning for that (repository, table) pair only; a
/// new repository that starts sharing a grandfathered table is not covered by the
/// existing attributes and still errors. The attribute is a TODO for migration,
/// not a permanent exemption.
/// </para>
/// <para>
/// Counting unit is top-level types implementing <c>IRepository</c>. Framework
/// stores (ASP.NET Identity <c>UserStore</c> etc.) are not repositories and do not
/// count — their DbContext access is governed by HUM0009. Runs in
/// <c>Humans.Infrastructure</c> only, the one compilation where
/// <c>HumansDbContext</c> and every repository implementation are visible.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SingleRepositoryPerTableAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0025";

    private const string HumansDbContextFullName = "Humans.Infrastructure.Data.HumansDbContext";
    private const string DbSetOpenTypeFullName = "Microsoft.EntityFrameworkCore.DbSet`1";
    private const string RepositoryMarkerFullName = "Humans.Application.Interfaces.Repositories.IRepository";

    private static readonly LocalizableString Title =
        "A DbSet table must be referenced by exactly one repository";

    private static readonly LocalizableString MessageFormat =
        "'{0}' references HumansDbContext.{1}, which is referenced by {2} repositories ({3}). A table must belong to exactly one repository — route foreign access through the owning section's service (I<Section>ServiceRead).";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Each section owns its tables and a table must exist in exactly one repository (peters-hard-rules.md). " +
            "When more than one repository references a HumansDbContext DbSet — read or write — the table is shared " +
            "across the repository (and usually the section) boundary, bypassing the owning section's service and its " +
            "§15 cache. Route foreign access through the owning section's IRepository / application service. Existing " +
            "shared tables carry [Grandfathered(\"HUM0025\", …, scope: \"<DbSet>\")] on each participating repository, " +
            "which downgrades the diagnostic to a warning for that (repository, table) pair only — the attribute is a " +
            "TODO for migration, not a permanent exemption.",
        // Reported from a CompilationEndAction: the N>1 count is only known once
        // every repository's DbSet references across the compilation are collected.
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Infrastructure, StringComparison.Ordinal))
            return;

        var dbContextType = context.Compilation.GetTypeByMetadataName(HumansDbContextFullName);
        if (dbContextType is null)
            return;

        var dbSetOpenType = context.Compilation.GetTypeByMetadataName(DbSetOpenTypeFullName);
        if (dbSetOpenType is null)
            return;

        var repositoryMarker = context.Compilation.GetTypeByMetadataName(RepositoryMarkerFullName);
        if (repositoryMarker is null)
            return;

        // entity → DbSet property name, so ctx.Set<TEntity>() resolves to the
        // same table identity as a direct ctx.<DbSet> reference.
        var dbSetNameByEntity = BuildDbSetNameByEntity(dbContextType, dbSetOpenType);

        var accesses = new ConcurrentBag<TableAccess>();

        context.RegisterOperationAction(
            ctx => CollectPropertyReference(ctx, dbContextType, dbSetOpenType, repositoryMarker, accesses),
            OperationKind.PropertyReference);

        context.RegisterOperationAction(
            ctx => CollectSetInvocation(ctx, dbContextType, repositoryMarker, dbSetNameByEntity, accesses),
            OperationKind.Invocation);

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterCompilationEndAction(ctx => Report(ctx, accesses, grandfatheredAttr));
    }

    private static void CollectPropertyReference(
        OperationAnalysisContext context,
        INamedTypeSymbol dbContextType,
        INamedTypeSymbol dbSetOpenType,
        INamedTypeSymbol repositoryMarker,
        ConcurrentBag<TableAccess> accesses)
    {
        var op = (IPropertyReferenceOperation)context.Operation;

        // Must be a DbSet<T> property…
        if (op.Property.Type is not INamedTypeSymbol propertyType
            || !SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, dbSetOpenType))
        {
            return;
        }

        // …accessed on a HumansDbContext receiver. Keying off the receiver type
        // (rather than the declaring type) catches DbSets inherited from
        // IdentityDbContext such as Users.
        if (!IsDbContextReceiver(op.Instance, dbContextType))
            return;

        var repository = OwningRepository(context.ContainingSymbol, repositoryMarker);
        if (repository is null)
            return;

        accesses.Add(new TableAccess(op.Property.Name, repository, op.Syntax.GetLocation()));
    }

    private static void CollectSetInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol dbContextType,
        INamedTypeSymbol repositoryMarker,
        IReadOnlyDictionary<string, string> dbSetNameByEntity,
        ConcurrentBag<TableAccess> accesses)
    {
        var op = (IInvocationOperation)context.Operation;
        if (!string.Equals(op.TargetMethod.Name, "Set", StringComparison.Ordinal)
            || op.TargetMethod.TypeArguments.Length != 1)
        {
            return;
        }

        if (!IsDbContextReceiver(op.Instance, dbContextType))
            return;

        if (op.TargetMethod.TypeArguments[0] is not INamedTypeSymbol entity
            || !dbSetNameByEntity.TryGetValue(SymbolKey(entity), out var dbSetName))
        {
            // No DbSet property for this entity — not a tracked table.
            return;
        }

        var repository = OwningRepository(context.ContainingSymbol, repositoryMarker);
        if (repository is null)
            return;

        accesses.Add(new TableAccess(dbSetName, repository, op.Syntax.GetLocation()));
    }

    private static void Report(
        CompilationAnalysisContext context,
        ConcurrentBag<TableAccess> accesses,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var byTable = new Dictionary<string, List<TableAccess>>(StringComparer.Ordinal);
        foreach (var access in accesses)
        {
            if (!byTable.TryGetValue(access.Table, out var list))
                byTable[access.Table] = list = [];
            list.Add(access);
        }

        foreach (var entry in byTable)
        {
            var table = entry.Key;
            var sites = entry.Value;

            var repositories = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var site in sites)
                repositories.Add(site.Repository);

            if (repositories.Count <= 1)
                continue;

            var repositoryList = string.Join(
                ", ",
                repositories.Select(r => r.Name).OrderBy(name => name, StringComparer.Ordinal));

            foreach (var site in sites)
            {
                var severity = GrandfatheredCheck.EffectiveSeverityForScope(
                    site.Repository, grandfatheredAttr, DiagnosticId, table);

                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor: Rule,
                    location: site.Location,
                    effectiveSeverity: severity,
                    additionalLocations: null,
                    properties: null,
                    messageArgs: [site.Repository.Name, table, repositories.Count, repositoryList]));
            }
        }
    }

    private static bool IsDbContextReceiver(IOperation? instance, INamedTypeSymbol dbContextType)
    {
        var type = Unwrap(instance)?.Type;
        if (type is null)
            return false;

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, dbContextType))
                return true;
        }

        return false;
    }

    private static INamedTypeSymbol? OwningRepository(ISymbol containingSymbol, INamedTypeSymbol repositoryMarker)
    {
        var top = containingSymbol.ContainingTopLevelType();
        if (top is null)
            return null;

        foreach (var iface in top.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, repositoryMarker))
                return top;
        }

        return null;
    }

    private static Dictionary<string, string> BuildDbSetNameByEntity(
        INamedTypeSymbol dbContextType,
        INamedTypeSymbol dbSetOpenType)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        for (ITypeSymbol? type = dbContextType; type is not null; type = type.BaseType)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is not IPropertySymbol property
                    || property.Type is not INamedTypeSymbol propertyType
                    || !SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, dbSetOpenType)
                    || propertyType.TypeArguments.Length != 1
                    || propertyType.TypeArguments[0] is not INamedTypeSymbol entity)
                {
                    continue;
                }

                var key = SymbolKey(entity);
                if (!map.ContainsKey(key))
                    map[key] = property.Name;
            }
        }

        return map;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        return operation;
    }

    private static string SymbolKey(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private readonly struct TableAccess(string table, INamedTypeSymbol repository, Location location)
    {
        public string Table { get; } = table;
        public INamedTypeSymbol Repository { get; } = repository;
        public Location Location { get; } = location;
    }
}
