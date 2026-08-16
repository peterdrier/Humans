using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers.Internal.Rules;

/// <summary>
/// HUM0035 — a repository never lives under <c>Contracts/</c>. Persistence is the
/// section's own business; putting it in the folder that means "public" makes another
/// section able to name it, which <c>internal</c> otherwise prevents.
/// </summary>
internal static class RepositoryPlacementRule
{
    public const string DiagnosticId = "HUM0035";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Repository declared under Contracts/",
        messageFormat:
            "'{0}' is a repository under Contracts/. Move it to the section's Data/ folder — "
            + "Contracts/ is what other sections may name, and persistence is not that.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Repositories are internal to their section so no other section can reach its "
            + "tables. Contracts/ is the one folder exempt from that, so a repository placed "
            + "there silently reopens the boundary.");

    public static void Analyze(
        SymbolAnalysisContext context,
        INamedTypeSymbol? repositoryMarker,
        INamedTypeSymbol? grandfatheredAttr)
    {
        if (repositoryMarker is null)
            return;

        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Interface))
            return;

        if (!SymbolEqualityComparer.Default.Equals(type, repositoryMarker)
            && !type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, repositoryMarker)))
        {
            return;
        }

        if (!PublicSurfaceRule.IsUnderContracts(type))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: type.Locations.Length > 0 ? type.Locations[0] : Location.None,
            effectiveSeverity: GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId),
            additionalLocations: null,
            properties: null,
            messageArgs: [type.Name]));
    }
}
