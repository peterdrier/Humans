using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0014 — a controller must not inject a repository. It calls the section's
/// application service (peters-hard-rules: "Controllers … can not call repositories").
/// </summary>
/// <remarks>
/// The subject was every class in <c>Humans.Web</c>; it is the controller itself now,
/// matched by <c>ControllerBase</c>. Deliberately no wider — who else inside a section
/// holds its own repository is that section's business, and cross-section access is
/// already impossible now that repositories are <c>internal</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WebRepositoryInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0014";

    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string IRepositoryFullName = "Humans.Base.Interfaces.Repositories.IRepository";

    private static readonly LocalizableString Title =
        "Controller injects a repository directly";

    private static readonly LocalizableString MessageFormat =
        "'{0}' injects '{1}'. A controller calls the section's application service; "
        + "the service owns the repository.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A repository is the persistence boundary its own section's application service " +
            "owns. A controller parses the request and calls the service; reaching past it " +
            "to the repository collapses the layer (design-rules §2b, §3).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var repositoryMarker = context.Compilation.GetTypeByMetadataName(IRepositoryFullName);
        if (repositoryMarker is null)
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            c => AnalyzeNamedType(c, repositoryMarker, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol repositoryMarker,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (!type.InheritsFromOrEquals(ControllerBaseFullName))
            return;

        // The grandfather decision is made on the containing class, not the
        // parameter — the [Grandfathered] attribute can only target a type.
        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (!Implements(parameter.Type, repositoryMarker))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor: Rule,
                    location: parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0],
                    effectiveSeverity: severity,
                    additionalLocations: null,
                    properties: null,
                    messageArgs: [type.Name, parameter.Type.Name]));
            }
        }
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol marker)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        if (SymbolEqualityComparer.Default.Equals(named, marker))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, marker))
                return true;
        }
        return false;
    }
}
