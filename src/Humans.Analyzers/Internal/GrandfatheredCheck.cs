using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Shared helper for the project's `[Grandfathered("HUM####", ...)]` attribute
/// — the only sanctioned suppression mechanism for HUM rules per
/// <c>memory/architecture/analyzer-exceptions-via-attributes.md</c>.
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
    public const string AttributeFullName = "Humans.Application.Architecture.GrandfatheredAttribute";

    /// <summary>
    /// Resolves the <see cref="GrandfatheredAttribute"/> symbol from the
    /// compilation. Returns <c>null</c> if the attribute type isn't reachable
    /// — in that case every diagnostic should fall through to its default
    /// severity (no grandfathering is possible).
    /// </summary>
    public static INamedTypeSymbol? Resolve(Compilation compilation) =>
        compilation.GetTypeByMetadataName(AttributeFullName);

    /// <summary>
    /// Returns <c>true</c> if <paramref name="type"/> carries
    /// <c>[Grandfathered("<paramref name="ruleId"/>", ...)]</c>.
    /// </summary>
    public static bool HasGrandfatherFor(
        INamedTypeSymbol type,
        INamedTypeSymbol? attributeSymbol,
        string ruleId)
    {
        if (attributeSymbol is null)
            return false;

        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeSymbol))
                continue;

            if (attr.ConstructorArguments.Length == 0)
                continue;

            var ruleIdArg = attr.ConstructorArguments[0];
            if (ruleIdArg.Value is string s &&
                string.Equals(s, ruleId, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Convenience: returns <see cref="DiagnosticSeverity.Warning"/> when
    /// the type is grandfathered for <paramref name="ruleId"/>, otherwise
    /// <see cref="DiagnosticSeverity.Error"/>.
    /// </summary>
    public static DiagnosticSeverity EffectiveSeverity(
        INamedTypeSymbol type,
        INamedTypeSymbol? attributeSymbol,
        string ruleId) =>
        HasGrandfatherFor(type, attributeSymbol, ruleId)
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
                !string.Equals(ruleIdValue, ruleId, System.StringComparison.Ordinal))
            {
                continue;
            }

            var scopeValue = args.Length >= 5 ? args[4].Value as string : null;
            if (scopeValue is not null && string.Equals(scopeValue, scope, System.StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Scoped counterpart of <see cref="EffectiveSeverity"/>: returns
    /// <see cref="DiagnosticSeverity.Warning"/> when the type is grandfathered
    /// for the (<paramref name="ruleId"/>, <paramref name="scope"/>) pair,
    /// otherwise <see cref="DiagnosticSeverity.Error"/>.
    /// </summary>
    public static DiagnosticSeverity EffectiveSeverityForScope(
        INamedTypeSymbol type,
        INamedTypeSymbol? attributeSymbol,
        string ruleId,
        string scope) =>
        HasGrandfatherForScope(type, attributeSymbol, ruleId, scope)
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Error;
}
