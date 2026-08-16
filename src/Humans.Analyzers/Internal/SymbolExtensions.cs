using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

internal static class SymbolExtensions
{
    public static bool InheritsFromOrEquals(this ITypeSymbol? type, string fullMetadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.ToDisplayString(), fullMetadataName, System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool NameStartsWith(this ITypeSymbol? type, string prefix)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name.StartsWith(prefix, System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the code doing the read/write is inside the entity that declares
    /// <paramref name="property"/> (or a type deriving from it) — <c>User</c>'s own
    /// <c>get =&gt; base.Email</c> / <c>set =&gt; base.Email = value</c> overrides and the
    /// derivations built on them.
    /// </summary>
    /// <remarks>
    /// Those rules police callers reaching past <c>IUserEmailService</c>. The entity that
    /// defines the derived behaviour is not a caller, and forwarding to <c>base</c> is the
    /// only way to write one.
    /// </remarks>
    public static bool IsInsideDeclaringEntity(this ISymbol? containingSymbol, IPropertySymbol property)
    {
        var declaringType = property.ContainingType?.ToDisplayString();
        return declaringType is not null
            && containingSymbol?.ContainingType.InheritsFromOrEquals(declaringType) == true;
    }

    public static INamedTypeSymbol? ContainingTopLevelType(this ISymbol symbol)
    {
        var t = symbol.ContainingType;
        while (t is { ContainingType: not null })
            t = t.ContainingType;
        return t;
    }
}
