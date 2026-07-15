using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Matcher for Humans persistence contexts (nobodies-collective/Humans#858).
/// Since the per-section DbContext split, the persistence boundary is no longer
/// the single <c>HumansDbContext</c> type but every class in
/// <c>Humans.Infrastructure.Data</c> deriving from
/// <c>Microsoft.EntityFrameworkCore.DbContext</c> (the main pile plus each
/// <c>&lt;Section&gt;DbContext</c>). Analyzers that police context access use this
/// helper instead of a hard-coded type name so peeled sections stay covered.
/// </summary>
internal static class SectionDbContexts
{
    public const string DataNamespace = "Humans.Infrastructure.Data";
    private const string EfDbContextFullName = "Microsoft.EntityFrameworkCore.DbContext";

    /// <summary>
    /// Resolves EF's <c>DbContext</c> base type in this compilation. Null when EF
    /// is unreferenced — no context type can exist there, so callers bail out.
    /// </summary>
    public static INamedTypeSymbol? ResolveEfDbContext(Compilation compilation) =>
        compilation.GetTypeByMetadataName(EfDbContextFullName);

    /// <summary>
    /// True when <paramref name="candidate"/> is a Humans persistence context:
    /// a class declared in <see cref="DataNamespace"/> whose base chain includes
    /// EF's <c>DbContext</c>.
    /// </summary>
    public static bool IsSectionDbContext(ITypeSymbol? candidate, INamedTypeSymbol efDbContext)
    {
        if (candidate is not INamedTypeSymbol named || named.TypeKind != TypeKind.Class)
            return false;

        if (!string.Equals(
                named.ContainingNamespace?.ToDisplayString(),
                DataNamespace,
                StringComparison.Ordinal))
        {
            return false;
        }

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
    /// Enumerates every Humans persistence context type declared in this
    /// compilation's <see cref="DataNamespace"/>.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateSectionDbContexts(
        Compilation compilation, INamedTypeSymbol efDbContext)
    {
        var ns = FindNamespace(compilation.Assembly.GlobalNamespace, DataNamespace);
        if (ns is null)
            yield break;

        foreach (var type in ns.GetTypeMembers())
        {
            if (IsSectionDbContext(type, efDbContext))
                yield return type;
        }
    }

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol root, string dottedName)
    {
        var current = root;
        foreach (var segment in dottedName.Split('.'))
        {
            INamespaceSymbol? next = null;
            foreach (var member in current.GetNamespaceMembers())
            {
                if (string.Equals(member.Name, segment, StringComparison.Ordinal))
                {
                    next = member;
                    break;
                }
            }

            if (next is null)
                return null;
            current = next;
        }

        return current;
    }
}
