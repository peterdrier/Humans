using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0020 — Caching services are decorators over an inner service plus cache
/// plumbing. They must not inject repositories directly.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CachingServiceRepositoryInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0020";

    private const string IRepositoryFullName = "Humans.Application.Interfaces.Repositories.IRepository";

    private static readonly LocalizableString Title =
        "Caching service injects a repository directly";

    private static readonly LocalizableString MessageFormat =
        "'{0}' injects '{1}'. Caching services should depend on their keyed inner service and cache plumbing, not repositories.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Caching services are decorators. Repository access and business decisions belong in " +
            "the scoped inner service; the cache layer should only decide whether a DTO/read model " +
            "is in the cache and when to invalidate it.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!AssemblyScope.IsApplicationWebOrInfrastructure(context.Compilation.Assembly))
            return;

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
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || !IsCachingService(type))
            return;

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

    private static bool IsCachingService(INamedTypeSymbol type) =>
        type.Name.StartsWith("Caching", System.StringComparison.Ordinal) &&
        type.Name.EndsWith("Service", System.StringComparison.Ordinal);

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
