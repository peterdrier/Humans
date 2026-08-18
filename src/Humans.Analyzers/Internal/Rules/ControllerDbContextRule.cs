using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers.Internal.Rules;

internal static class ControllerDbContextRule
{
    public const string DiagnosticId = "HUM0008";

    private static readonly LocalizableString Title =
        "Controllers may not inject an application DbContext";

    private static readonly LocalizableString MessageFormat =
        "Controller '{0}' must not inject '{1}'. Controllers should call services; services go through repositories or infrastructure-owned database services.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Controllers reaching directly for an application DbContext (any per-section " +
            "context) bypass the service and repository layers. Keep database access behind an application or " +
            "infrastructure service and inject that service instead.");

    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";

    public static void Register(CompilationStartAnalysisContext context)
    {
        // Since the per-section split (nobodies-collective/Humans#858) the persistence
        // boundary is every application context, matched structurally via
        // SectionDbContexts (derives from EF's DbContext) rather than by name or
        // namespace, so relocating the contexts cannot switch this rule off.
        var efDbContext = SectionDbContexts.ResolveEfDbContext(context.Compilation);
        if (efDbContext is null)
            return;

        context.RegisterSymbolAction(c => AnalyzeNamedType(c, efDbContext), SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol efDbContext)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.InheritsFromOrEquals(ControllerBaseFullName))
            return;

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (!SectionDbContexts.IsSectionDbContext(parameter.Type, efDbContext))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0],
                    type.Name,
                    parameter.Type.Name));
            }
        }
    }
}
