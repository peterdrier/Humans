using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Matches every Humans persistence context structurally: a class whose base chain reaches EF's <c>DbContext</c>.
/// Pins neither namespace nor assembly, so relocating the contexts cannot switch the analyzers off.
/// </summary>
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
    /// EF's own <c>DbContext</c> does not match (the walk starts at the base type), so plumbing that takes the
    /// framework base class, like the section migration runner, is not mistaken for a context.
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
    /// Walks generic arguments and array elements too: <c>IDbContextFactory&lt;TContext&gt;</c>,
    /// <c>UserStore&lt;…, UsersDbContext, …&gt;</c>.
    /// </summary>
    public static bool ReferencesSectionDbContext(ITypeSymbol? candidate, INamedTypeSymbol efDbContext) =>
        FindReferencedSectionDbContext(candidate, efDbContext) is not null;

    /// <summary>
    /// Same walk as <see cref="ReferencesSectionDbContext"/>, returning the matched context so a diagnostic can name it.
    /// </summary>
    public static INamedTypeSymbol? FindReferencedSectionDbContext(ITypeSymbol? candidate, INamedTypeSymbol efDbContext)
    {
        if (candidate is null)
            return null;

        if (IsSectionDbContext(candidate, efDbContext))
            return (INamedTypeSymbol)candidate;

        if (candidate is INamedTypeSymbol named)
        {
            foreach (var typeArg in named.TypeArguments)
            {
                var found = FindReferencedSectionDbContext(typeArg, efDbContext);
                if (found is not null)
                    return found;
            }
        }

        if (candidate is IArrayTypeSymbol arr)
            return FindReferencedSectionDbContext(arr.ElementType, efDbContext);

        return null;
    }

    /// <summary>
    /// This compilation's own assembly only: framework contexts (Identity, Data Protection) are not ours to police.
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
