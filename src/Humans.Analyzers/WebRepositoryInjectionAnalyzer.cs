using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0014 — No class in <c>Humans.Web</c> may inject a repository directly.
/// Web depends on application services; services depend on repositories.
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Web</c> only. Replaces the reflection test
/// <c>ServiceBoundaryArchitectureTests.Web_classes_do_not_inject_repositories</c>
/// with compile-time enforcement.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WebRepositoryInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0014";

    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string IRepositoryFullName = "Humans.Application.Interfaces.Repositories.IRepository";

    private static readonly LocalizableString Title =
        "Web class injects a repository directly";

    private static readonly LocalizableString MessageFormat =
        "'{0}' injects '{1}'. Web depends on application services, not persistence repositories.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Classes in Humans.Web (controllers, view components, filters, etc.) must call " +
            "application services, never repositories directly. Repositories are the persistence " +
            "boundary owned by Application/Infrastructure; reaching past services collapses the " +
            "layer (design-rules §2b, §3).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!AssemblyScope.IsLayerOrSection(context.Compilation.Assembly, AssemblyScope.Web))
            return;

        var repositoryMarker = context.Compilation.GetTypeByMetadataName(IRepositoryFullName);
        if (repositoryMarker is null)
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        // In Humans.Web every class is Web-layer, so assembly identity is the whole test.
        // A section assembly holds all three layers at once (nobodies-collective/Humans#866),
        // so there the subject has to be identified structurally — otherwise the section's
        // own service, whose entire job is to hold the repository, trips the rule.
        var controllersOnly = !string.Equals(
            context.Compilation.Assembly.Name, AssemblyScope.Web, System.StringComparison.Ordinal);

        context.RegisterSymbolAction(
            c => AnalyzeNamedType(c, repositoryMarker, grandfatheredAttr, controllersOnly),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol repositoryMarker,
        INamedTypeSymbol? grandfatheredAttr,
        bool controllersOnly)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (controllersOnly && !type.InheritsFromOrEquals(ControllerBaseFullName))
            return;

        // The grandfather decision is made on the containing class, not the
        // parameter — the [Grandfathered] attribute can only target a type.
        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (!ImplementsRepositoryMarker(parameter.Type, repositoryMarker))
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

    private static bool ImplementsRepositoryMarker(ITypeSymbol type, INamedTypeSymbol repositoryMarker)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        if (SymbolEqualityComparer.Default.Equals(named, repositoryMarker))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, repositoryMarker))
                return true;
        }
        return false;
    }
}
