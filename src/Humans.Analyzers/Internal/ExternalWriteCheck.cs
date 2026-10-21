using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Shared lookup for <c>[ExternalWrite]</c>, the marker that carries "this
/// reaches a write to a third-party system" across the assembly boundary in
/// metadata. Used by HUM0033 and HUM0038.
/// </summary>
internal static class ExternalWriteCheck
{
    /// <summary>
    /// True when the invoked method — or, for a call made through a concrete
    /// class, an interface member it implements — carries <c>[ExternalWrite]</c>.
    /// </summary>
    public static bool IsExternalWrite(IMethodSymbol method, INamedTypeSymbol attribute)
    {
        if (HasAttribute(method, attribute))
            return true;

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                if (member is not IMethodSymbol ifaceMethod)
                    continue;

                var impl = method.ContainingType.FindImplementationForInterfaceMember(ifaceMethod);
                if (SymbolEqualityComparer.Default.Equals(impl, method) &&
                    HasAttribute(ifaceMethod, attribute))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attribute))
                return true;
        }

        return false;
    }
}
