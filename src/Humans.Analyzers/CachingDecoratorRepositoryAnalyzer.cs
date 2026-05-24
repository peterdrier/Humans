using System.Collections.Generic;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0020 - Caching decorators resolve cache misses and warm paths through
/// their keyed inner application service, not by injecting or calling
/// repositories directly.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CachingDecoratorRepositoryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0020";

    private const string InfrastructureServicesPrefix = "Humans.Infrastructure.Services";
    private const string IRepositoryFullName = "Humans.Application.Interfaces.Repositories.IRepository";

    private static readonly LocalizableString Title =
        "Caching decorator references a repository directly";

    private static readonly LocalizableString MessageFormat =
        "'{0}' references repository '{1}'. Caching decorators must route misses and warm paths through the keyed inner service.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Caching decorators are transparent infrastructure wrappers around an application service. " +
            "They may cache, invalidate, and delegate to the keyed inner service, but must not receive or " +
            "hold repositories directly; doing so bypasses the inner service's authorization, section " +
            "boundaries, orchestration, and cache-invalidation behavior. Existing violators carry " +
            "[Grandfathered(\"HUM0020\", ...)] until their warm paths are migrated.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Infrastructure, System.StringComparison.Ordinal))
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
        if (type.TypeKind != TypeKind.Class)
            return;

        var decorator = FindContainingCachingDecorator(type);
        if (decorator is null)
            return;

        foreach (var reference in FindRepositoryReferences(type, repositoryMarker))
        {
            Report(context, decorator, grandfatheredAttr, reference.Location, reference.RepositoryName);
        }
    }

    private static IEnumerable<(Location Location, string RepositoryName)> FindRepositoryReferences(
        INamedTypeSymbol type,
        INamedTypeSymbol repositoryMarker)
    {
        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (ImplementsRepositoryMarker(parameter.Type, repositoryMarker))
                    yield return (PreferFirst(parameter.Locations, ctor.Locations), parameter.Type.Name);
            }
        }

        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            switch (member)
            {
                case IFieldSymbol field when ImplementsRepositoryMarker(field.Type, repositoryMarker):
                    yield return (PreferFirst(field.Locations, type.Locations), field.Type.Name);
                    break;

                case IPropertySymbol property when ImplementsRepositoryMarker(property.Type, repositoryMarker):
                    yield return (PreferFirst(property.Locations, type.Locations), property.Type.Name);
                    break;

                case IMethodSymbol method:
                    if (method.MethodKind == MethodKind.Constructor)
                        break;
                    if (method.AssociatedSymbol is IPropertySymbol)
                        break;

                    if (ImplementsRepositoryMarker(method.ReturnType, repositoryMarker))
                        yield return (PreferFirst(method.Locations, type.Locations), method.ReturnType.Name);

                    foreach (var parameter in method.Parameters)
                    {
                        if (ImplementsRepositoryMarker(parameter.Type, repositoryMarker))
                            yield return (PreferFirst(parameter.Locations, method.Locations), parameter.Type.Name);
                    }
                    break;
            }
        }
    }

    private static INamedTypeSymbol? FindContainingCachingDecorator(INamedTypeSymbol type)
    {
        var topLevel = type;
        while (topLevel.ContainingType is not null)
            topLevel = topLevel.ContainingType;

        if (!topLevel.Name.StartsWith("Caching", System.StringComparison.Ordinal)
            || !topLevel.Name.EndsWith("Service", System.StringComparison.Ordinal))
            return null;

        var ns = topLevel.ContainingNamespace?.ToDisplayString();
        if (ns is null)
            return null;

        if (string.Equals(ns, InfrastructureServicesPrefix, System.StringComparison.Ordinal)
            || ns.StartsWith(InfrastructureServicesPrefix + ".", System.StringComparison.Ordinal))
            return topLevel;

        return null;
    }

    private static bool ImplementsRepositoryMarker(ITypeSymbol? type, INamedTypeSymbol repositoryMarker)
    {
        if (type is ITypeParameterSymbol typeParameter)
        {
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                if (ImplementsRepositoryMarker(constraint, repositoryMarker))
                    return true;
            }

            return false;
        }

        if (type is not INamedTypeSymbol named)
            return false;

        if (SymbolEqualityComparer.Default.Equals(named, repositoryMarker))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, repositoryMarker))
                return true;
        }

        foreach (var argument in named.TypeArguments)
        {
            if (ImplementsRepositoryMarker(argument, repositoryMarker))
                return true;
        }

        return false;
    }

    private static Location PreferFirst(ImmutableArray<Location> primary, ImmutableArray<Location> fallback)
    {
        if (primary.Length > 0)
            return primary[0];
        if (fallback.Length > 0)
            return fallback[0];
        return Location.None;
    }

    private static void Report(
        SymbolAnalysisContext context,
        INamedTypeSymbol decorator,
        INamedTypeSymbol? grandfatheredAttr,
        Location location,
        string repositoryName)
    {
        var severity = GrandfatheredCheck.EffectiveSeverity(decorator, grandfatheredAttr, DiagnosticId);
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: location,
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: [decorator.Name, repositoryName]));
    }
}
