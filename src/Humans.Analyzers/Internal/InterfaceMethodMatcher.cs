using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

internal static class InterfaceMethodMatcher
{
    /// <summary>
    /// Returns true when <paramref name="method"/> is the interface method
    /// <c><paramref name="interfaceFullName"/>.<paramref name="methodName"/></c>
    /// OR when it is a concrete implementation of that interface method on
    /// any class.
    ///
    /// <para>
    /// Without this, an analyzer that pins a call site on its interface name
    /// (e.g. <c>IUserEmailService.UpdateEmailAsync</c>) misses callers that
    /// hold the concrete implementation type — DI rebinding the same service
    /// to its concrete class would silently bypass the rule.
    /// </para>
    /// </summary>
    public static bool Targets(
        IMethodSymbol method,
        string interfaceFullName,
        string methodName)
    {
        if (!string.Equals(method.Name, methodName, System.StringComparison.Ordinal))
            return false;

        var containingType = method.ContainingType;
        if (containingType is null)
            return false;

        // Direct interface call.
        if (string.Equals(containingType.ToDisplayString(), interfaceFullName, System.StringComparison.Ordinal))
            return true;

        // Concrete-type or derived-class call — resolve via the interface map.
        foreach (var iface in containingType.AllInterfaces)
        {
            if (!string.Equals(iface.ToDisplayString(), interfaceFullName, System.StringComparison.Ordinal) &&
                !string.Equals(iface.OriginalDefinition.ToDisplayString(), interfaceFullName, System.StringComparison.Ordinal))
                continue;

            foreach (var iMember in iface.GetMembers(methodName).OfType<IMethodSymbol>())
            {
                var impl = containingType.FindImplementationForInterfaceMember(iMember);
                if (impl is null) continue;

                if (SymbolEqualityComparer.Default.Equals(impl, method) ||
                    SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, method.OriginalDefinition))
                    return true;
            }
        }

        return false;
    }
}
