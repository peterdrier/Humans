using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Matcher for Humans persistence contexts (nobodies-collective/Humans#858).
/// Since the per-section DbContext split, the persistence boundary is no longer
/// the single <c>HumansDbContext</c> type but every application context — the
/// main pile plus each <c>&lt;Section&gt;DbContext</c>. Analyzers that police
/// context access use this helper instead of a hard-coded type name so peeled
/// sections stay covered.
/// </summary>
/// <remarks>
/// Detection is <b>structural</b>: a class counts when its base chain reaches
/// <c>Microsoft.EntityFrameworkCore.DbContext</c>. It deliberately pins neither
/// namespace nor assembly, so relocating the contexts cannot silently switch
/// the analyzers off. Scoping to production code is unchanged and lives in the
/// analyzers themselves — each bails unless the compilation is the assembly it
/// polices (<see cref="AssemblyScope"/>), so test doubles deriving from
/// <c>DbContext</c> are never analyzed.
/// </remarks>
internal static class SectionDbContexts
{
    private const string EfDbContextFullName = "Microsoft.EntityFrameworkCore.DbContext";

    /// <summary>
    /// Resolves EF's <c>DbContext</c> base type in this compilation. Null when EF
    /// is unreferenced — no context type can exist there, so callers bail out.
    /// </summary>
    public static INamedTypeSymbol? ResolveEfDbContext(Compilation compilation) =>
        compilation.GetTypeByMetadataName(EfDbContextFullName);

    /// <summary>
    /// True when <paramref name="candidate"/> is a Humans persistence context:
    /// a class whose base chain includes EF's <c>DbContext</c>. EF's own
    /// <c>DbContext</c> does not match — the walk starts at the base type — so
    /// infrastructure plumbing that takes the framework base class (the section
    /// migration runner, for one) is not mistaken for a context.
    /// </summary>
    public static bool IsSectionDbContext(ITypeSymbol? candidate, INamedTypeSymbol efDbContext)
    {
        if (candidate is not INamedTypeSymbol named || named.TypeKind != TypeKind.Class)
            return false;

        for (ITypeSymbol? current = named.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, efDbContext))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively walks a type and its generic arguments / array elements,
    /// looking for any Humans persistence context. Handles
    /// <c>IDbContextFactory&lt;TContext&gt;</c>, <c>UserStore&lt;…, HumansDbContext, …&gt;</c>,
    /// and arbitrarily-nested constructions.
    /// </summary>
    public static bool ReferencesSectionDbContext(ITypeSymbol? candidate, INamedTypeSymbol efDbContext)
    {
        if (candidate is null)
            return false;

        if (IsSectionDbContext(candidate, efDbContext))
            return true;

        if (candidate is INamedTypeSymbol named)
        {
            foreach (var typeArg in named.TypeArguments)
            {
                if (ReferencesSectionDbContext(typeArg, efDbContext))
                    return true;
            }
        }

        if (candidate is IArrayTypeSymbol arr && ReferencesSectionDbContext(arr.ElementType, efDbContext))
            return true;

        return false;
    }

    /// <summary>
    /// Enumerates every persistence context declared in this compilation's own
    /// assembly, at any namespace depth. Referenced assemblies are out of scope:
    /// framework contexts (Identity, Data Protection) are not ours to police.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateSectionDbContexts(
        Compilation compilation, INamedTypeSymbol efDbContext)
    {
        foreach (var type in DeclaredTypes(compilation.Assembly.GlobalNamespace))
        {
            if (IsSectionDbContext(type, efDbContext))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> DeclaredTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            yield return type;

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in DeclaredTypes(child))
                yield return type;
        }
    }
}
