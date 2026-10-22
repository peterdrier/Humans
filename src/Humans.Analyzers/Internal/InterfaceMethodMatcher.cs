using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

internal static class InterfaceMethodMatcher
{
    /// <summary>
    /// True for the interface method <c><paramref name="interfaceFullName"/>.<paramref name="methodName"/></c> or any
    /// concrete implementation of it, so DI rebinding a service to its concrete class can't bypass a rule.
    /// </summary>
    public static bool Targets(
        IMethodSymbol method,
        string interfaceFullName,
        string methodName)
    {
        if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            return false;

        var containingType = method.ContainingType;
        if (containingType is null)
            return false;

        if (string.Equals(containingType.ToDisplayString(), interfaceFullName, StringComparison.Ordinal))
            return true;

        // Concrete-type or derived-class call — resolve via the interface map.
        foreach (var iface in containingType.AllInterfaces)
        {
            if (!string.Equals(iface.ToDisplayString(), interfaceFullName, StringComparison.Ordinal) &&
                !string.Equals(iface.OriginalDefinition.ToDisplayString(), interfaceFullName, StringComparison.Ordinal))
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
