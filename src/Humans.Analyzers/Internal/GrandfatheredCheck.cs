using Humans.Base.Attributes;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// The only sanctioned suppression for HUM rules
/// (<c>memory/architecture/analyzer-exceptions-via-attributes.md</c>).
/// </summary>
/// <remarks>
/// Typical usage in an analyzer:
/// <code>
/// // In OnCompilationStart:
/// var grandfathered = GrandfatheredCheck.Resolve(context.Compilation);
///
/// // Before ReportDiagnostic:
/// var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfathered, DiagnosticId);
/// context.ReportDiagnostic(Diagnostic.Create(
///     descriptor: Rule,
///     location: location,
///     effectiveSeverity: severity,
///     additionalLocations: null,
///     properties: null,
///     messageArgs: ...));
/// </code>
/// </remarks>
internal static class GrandfatheredCheck
{
    /// <summary>
    /// Derived from the class itself, so a namespace move carries the constant with it. Never re-inline it as a literal.
    /// </summary>
    public static readonly string AttributeFullName = typeof(GrandfatheredAttribute).FullName!;

    /// <summary>Null when the attribute type isn't reachable: every diagnostic then keeps its default severity.</summary>
    public static INamedTypeSymbol? Resolve(Compilation compilation) =>
        compilation.GetTypeByMetadataName(AttributeFullName);

    /// <summary>
    /// Returns <c>true</c> if <paramref name="symbol"/> (a type for
    /// type-level rules, a method for method-level rules like HUM0031)
    /// carries <c>[Grandfathered("<paramref name="ruleId"/>", ...)]</c>.
    /// </summary>
    public static bool HasGrandfatherFor(
        ISymbol symbol,
        INamedTypeSymbol? attributeSymbol,
        string ruleId)
    {
        if (attributeSymbol is null)
            return false;

        foreach (var attr in symbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeSymbol))
                continue;

            if (attr.ConstructorArguments.Length == 0)
                continue;

            var ruleIdArg = attr.ConstructorArguments[0];
            if (ruleIdArg.Value is string s &&
                string.Equals(s, ruleId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Warning when the symbol is grandfathered for <paramref name="ruleId"/>, otherwise Error.</summary>
    public static DiagnosticSeverity EffectiveSeverity(
        ISymbol symbol,
        INamedTypeSymbol? attributeSymbol,
        string ruleId) =>
        HasGrandfatherFor(symbol, attributeSymbol, ruleId)
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Error;

    /// <summary>
    /// Returns <c>true</c> if <paramref name="type"/> carries
    /// <c>[Grandfathered("<paramref name="ruleId"/>", …, scope: "<paramref name="scope"/>")]</c>.
    /// Used by rules that grandfather at a finer granularity than the whole type
    /// (HUM0025: a (repository, DbSet) pair). The <c>scope</c> is the 5th
    /// positional constructor argument; an attribute applied with the 4-argument
    /// shape (no scope) never matches a scoped query.
    /// </summary>
    public static bool HasGrandfatherForScope(
        INamedTypeSymbol type,
        INamedTypeSymbol? attributeSymbol,
        string ruleId,
        string scope)
    {
        if (attributeSymbol is null)
            return false;

        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeSymbol))
                continue;

            var args = attr.ConstructorArguments;
            if (args.Length == 0)
                continue;

            if (args[0].Value is not string ruleIdValue ||
                !string.Equals(ruleIdValue, ruleId, StringComparison.Ordinal))
            {
                continue;
            }

            var scopeValue = args.Length >= 5 ? args[4].Value as string : null;
            if (scopeValue is not null && string.Equals(scopeValue, scope, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Scoped counterpart of <see cref="EffectiveSeverity"/>.</summary>
    public static DiagnosticSeverity EffectiveSeverityForScope(
        INamedTypeSymbol type,
        INamedTypeSymbol? attributeSymbol,
        string ruleId,
        string scope) =>
        HasGrandfatherForScope(type, attributeSymbol, ruleId, scope)
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Error;
}
