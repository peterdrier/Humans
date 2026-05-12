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

    public static INamedTypeSymbol? ContainingTopLevelType(this ISymbol symbol)
    {
        var t = symbol.ContainingType;
        while (t is { ContainingType: not null })
            t = t.ContainingType;
        return t;
    }
}
