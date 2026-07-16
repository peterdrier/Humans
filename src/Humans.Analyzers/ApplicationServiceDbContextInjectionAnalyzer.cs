using System;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0009 — Only repository classes (transitive implementers of
/// <c>Humans.Application.Interfaces.Repositories.IRepository</c>) may use
/// <c>HumansDbContext</c>. Every other class must go through a repository or
/// service. Existing pre-rule violators may carry
/// <c>[Grandfathered("HUM0009", …)]</c> — the analyzer downgrades the
/// diagnostic to <c>Warning</c> for those, but new violators (no attribute)
/// fire <c>Error</c>.
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Infrastructure</c> only. That's the only compilation
/// where both <c>HumansDbContext</c> and every candidate user type are
/// declared: Application has no project reference to Infrastructure so
/// <c>HumansDbContext</c> is unresolvable there, and Web's analysis only sees
/// types declared in Web (controllers).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationServiceDbContextInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0009";

    private static readonly LocalizableString Title =
        "Non-repository class uses HumansDbContext";

    private static readonly LocalizableString MessageFormat =
        "'{0}' uses HumansDbContext but does not implement IRepository. Route the DB access through a repository or service.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "HumansDbContext is the persistence boundary; only repositories may touch it. " +
            "Classes outside the repository layer that need persisted state must call a service " +
            "or repository. Existing violators may carry [Grandfathered(\"HUM0009\", …)] which " +
            "downgrades this diagnostic to a warning for the tagged class only — the attribute " +
            "is a TODO for migration, not a permanent exemption.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    private const string IRepositoryFullName = "Humans.Application.Interfaces.Repositories.IRepository";
    private const string DesignTimeDbContextFactoryFullName =
        "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory`1";
    private const string HostedLifecycleServiceFullName =
        "Microsoft.Extensions.Hosting.IHostedLifecycleService";
    private const string InfrastructureHostingNamespace = "Humans.Infrastructure.Hosting";

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Infrastructure, StringComparison.Ordinal))
            return;

        // Since the per-section split (nobodies-collective/Humans#858) the persistence
        // boundary is every context in Humans.Infrastructure.Data, matched structurally
        // via SectionDbContexts rather than by the single HumansDbContext name.
        var efDbContext = SectionDbContexts.ResolveEfDbContext(context.Compilation);
        if (efDbContext is null)
            return;

        var repositoryMarker = context.Compilation.GetTypeByMetadataName(IRepositoryFullName);
        if (repositoryMarker is null)
            return;

        var designTimeDbContextFactory = context.Compilation.GetTypeByMetadataName(DesignTimeDbContextFactoryFullName);
        var hostedLifecycleService = context.Compilation.GetTypeByMetadataName(HostedLifecycleServiceFullName);
        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(
                ctx,
                efDbContext,
                repositoryMarker,
                designTimeDbContextFactory,
                hostedLifecycleService,
                grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol efDbContext,
        INamedTypeSymbol repositoryMarker,
        INamedTypeSymbol? designTimeDbContextFactory,
        INamedTypeSymbol? hostedLifecycleService,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class)
            return;

        // Don't flag the context types themselves, EF migration files, or
        // types generated by the EF designer.
        if (SectionDbContexts.IsSectionDbContext(type, efDbContext))
            return;

        if (IsAllowedDbContextBoundary(
                type,
                efDbContext,
                designTimeDbContextFactory,
                hostedLifecycleService))
        {
            return;
        }

        // Allow repository implementations — the rule's whole point is "use
        // a repository instead."
        if (ImplementsRepositoryMarker(type, repositoryMarker))
            return;

        var location = FindFirstDbContextReference(type, efDbContext);
        if (location is null)
            return;

        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: location,
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: type.Name));
    }

    private static bool ImplementsRepositoryMarker(INamedTypeSymbol type, INamedTypeSymbol repositoryMarker)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, repositoryMarker))
                return true;
        }
        return false;
    }

    private static bool IsAllowedDbContextBoundary(
        INamedTypeSymbol type,
        INamedTypeSymbol efDbContext,
        INamedTypeSymbol? designTimeDbContextFactory,
        INamedTypeSymbol? hostedLifecycleService)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (designTimeDbContextFactory is not null &&
                SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, designTimeDbContextFactory) &&
                iface.TypeArguments.Length == 1 &&
                SectionDbContexts.IsSectionDbContext(iface.TypeArguments[0], efDbContext))
            {
                return true;
            }

            if (hostedLifecycleService is not null &&
                SymbolEqualityComparer.Default.Equals(iface, hostedLifecycleService) &&
                string.Equals(
                    type.ContainingNamespace?.ToDisplayString(),
                    InfrastructureHostingNamespace,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the first structural reference to a Humans persistence context on
    /// the type — base type, implemented interface, field, property, or method
    /// (parameter or return type). Recurses through generic type arguments
    /// so <c>UserStore&lt;…, HumansDbContext, …&gt;</c> also matches.
    /// Returns null if the type does not use a context structurally.
    /// </summary>
    private static Location? FindFirstDbContextReference(INamedTypeSymbol type, INamedTypeSymbol efDbContext)
    {
        if (type.BaseType is { } baseType && SectionDbContexts.ReferencesSectionDbContext(baseType, efDbContext))
            return PreferFirstSyntax(type);

        foreach (var iface in type.Interfaces)
        {
            if (SectionDbContexts.ReferencesSectionDbContext(iface, efDbContext))
                return PreferFirstSyntax(type);
        }

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field when SectionDbContexts.ReferencesSectionDbContext(field.Type, efDbContext):
                    return PreferFirst(field.Locations);

                case IPropertySymbol prop when SectionDbContexts.ReferencesSectionDbContext(prop.Type, efDbContext):
                    return PreferFirst(prop.Locations);

                case IMethodSymbol method:
                    if (SectionDbContexts.ReferencesSectionDbContext(method.ReturnType, efDbContext))
                        return PreferFirst(method.Locations);

                    foreach (var parameter in method.Parameters)
                    {
                        if (SectionDbContexts.ReferencesSectionDbContext(parameter.Type, efDbContext))
                            return PreferFirst(parameter.Locations);
                    }
                    break;
            }
        }

        return null;
    }

    private static Location? PreferFirstSyntax(INamedTypeSymbol type) =>
        type.Locations.Length > 0 ? type.Locations[0] : null;

    private static Location? PreferFirst(ImmutableArray<Location> locations) =>
        locations.Length > 0 ? locations[0] : null;
}
