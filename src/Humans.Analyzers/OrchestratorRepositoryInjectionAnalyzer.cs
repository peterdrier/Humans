using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0026 / HUM0027 — role-marker analyzer pair for
/// <see cref="Humans.Application.Interfaces.IOrchestrator"/>.
/// <list type="bullet">
/// <item><b>HUM0026</b> — an <c>IOrchestrator</c> implementer must not inject
/// any <c>I*Repository</c>, <c>HumansDbContext</c>, or
/// <c>IDbContextFactory&lt;HumansDbContext&gt;</c>. Orchestrators own no lane.</item>
/// <item><b>HUM0027</b> — a type must implement <c>IOrchestrator</c> XOR
/// <c>IApplicationService</c>, never both. The role axis is exclusive.</item>
/// </list>
/// Both are <see cref="DiagnosticSeverity.Error"/> by default and carry no
/// grandfather machinery — a genuine orchestrator never violates either rule,
/// and a violator means the role marker is wrong (relocate the access into
/// the owning section's Section service, not grandfather here).
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Application</c> only. The Application compilation is the
/// one place where both markers and every candidate orchestrator are declared.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OrchestratorRepositoryInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string RepositoryInjectionDiagnosticId = "HUM0026";
    public const string RoleConflictDiagnosticId = "HUM0027";

    private const string OrchestratorMarkerFullName = "Humans.Application.Interfaces.IOrchestrator";
    private const string ApplicationServiceMarkerFullName = "Humans.Application.Interfaces.IApplicationService";
    private const string RepositoryMarkerFullName = "Humans.Application.Interfaces.Repositories.IRepository";
    private const string HumansDbContextFullName = "Humans.Infrastructure.Data.HumansDbContext";
    private const string DbContextFactoryFullName = "Microsoft.EntityFrameworkCore.IDbContextFactory`1";

    private static readonly LocalizableString RepositoryInjectionTitle =
        "Orchestrator injects a repository or DbContext";

    private static readonly LocalizableString RepositoryInjectionMessageFormat =
        "'{0}' implements IOrchestrator but injects '{1}'. Orchestrators own no lane and must coordinate sections through their public service interfaces, not their repositories or DbContext.";

    private static readonly LocalizableString RoleConflictTitle =
        "Service implements both IApplicationService and IOrchestrator";

    private static readonly LocalizableString RoleConflictMessageFormat =
        "'{0}' implements both IApplicationService and IOrchestrator. The role axis is exclusive — a service is a Section (IApplicationService) or an Orchestrator (IOrchestrator), never both.";

    public static readonly DiagnosticDescriptor RepositoryInjectionRule = new(
        id: RepositoryInjectionDiagnosticId,
        title: RepositoryInjectionTitle,
        messageFormat: RepositoryInjectionMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "An IOrchestrator coordinates ≥2 sections through their public service interfaces " +
            "and owns no tables — by definition it must not inject any I*Repository, " +
            "HumansDbContext, or IDbContextFactory<HumansDbContext>. A type that needs that " +
            "access is a Section, not an orchestrator: relocate the access into the owning " +
            "section's Section service. No grandfather machinery — a violation here means " +
            "the role marker is wrong, not that the rule is wrong.");

    public static readonly DiagnosticDescriptor RoleConflictRule = new(
        id: RoleConflictDiagnosticId,
        title: RoleConflictTitle,
        messageFormat: RoleConflictMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The role axis is exclusive: IApplicationService grants own-lane repository " +
            "access; IOrchestrator forbids it. A service that carries both markers has a " +
            "self-contradicting role declaration. Pick one — Section (owns a lane) or " +
            "Orchestrator (coordinates other sections).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [RepositoryInjectionRule, RoleConflictRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Application, System.StringComparison.Ordinal))
            return;

        var orchestratorMarker = context.Compilation.GetTypeByMetadataName(OrchestratorMarkerFullName);
        if (orchestratorMarker is null)
            return;

        var applicationServiceMarker = context.Compilation.GetTypeByMetadataName(ApplicationServiceMarkerFullName);
        var repositoryMarker = context.Compilation.GetTypeByMetadataName(RepositoryMarkerFullName);
        // HumansDbContext lives in Humans.Infrastructure — usually unreachable from this
        // compilation. Resolves only when an orchestrator (illegally) takes a project
        // reference that drags it in. Same for IDbContextFactory<>.
        var dbContextType = context.Compilation.GetTypeByMetadataName(HumansDbContextFullName);
        var dbContextFactoryDef = context.Compilation.GetTypeByMetadataName(DbContextFactoryFullName);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(
                ctx,
                orchestratorMarker,
                applicationServiceMarker,
                repositoryMarker,
                dbContextType,
                dbContextFactoryDef),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol orchestratorMarker,
        INamedTypeSymbol? applicationServiceMarker,
        INamedTypeSymbol? repositoryMarker,
        INamedTypeSymbol? dbContextType,
        INamedTypeSymbol? dbContextFactoryDef)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (!ImplementsMarker(type, orchestratorMarker))
            return;

        // HUM0027 — exclusive role.
        if (applicationServiceMarker is not null && ImplementsMarker(type, applicationServiceMarker))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: RoleConflictRule,
                location: FirstLocation(type),
                messageArgs: type.Name));
        }

        // HUM0026 — no repository / DbContext / DbContextFactory<HumansDbContext> injection.
        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                var paramType = parameter.Type;

                var isRepo = repositoryMarker is not null && ImplementsMarker(paramType, repositoryMarker);
                var isDbContext = dbContextType is not null && TypeReferences(paramType, dbContextType);
                var isFactory = dbContextFactoryDef is not null && IsDbContextFactoryOf(paramType, dbContextFactoryDef, dbContextType);

                if (!isRepo && !isDbContext && !isFactory)
                    continue;

                var paramLocation = parameter.Locations.Length > 0
                    ? parameter.Locations[0]
                    : ctor.Locations.Length > 0 ? ctor.Locations[0] : FirstLocation(type);

                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor: RepositoryInjectionRule,
                    location: paramLocation,
                    messageArgs: [type.Name, paramType.ToDisplayString()]));
            }
        }
    }

    private static bool ImplementsMarker(ITypeSymbol type, INamedTypeSymbol marker)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        // Roslyn's AllInterfaces does not include the interface symbol itself,
        // so a parameter typed as the marker (e.g. bare `IRepository`) would
        // otherwise slip past this check. Equality first covers that case.
        if (SymbolEqualityComparer.Default.Equals(named, marker))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, marker))
                return true;
        }
        return false;
    }

    private static bool TypeReferences(ITypeSymbol? candidate, INamedTypeSymbol target)
    {
        if (candidate is null)
            return false;
        if (SymbolEqualityComparer.Default.Equals(candidate, target))
            return true;
        if (candidate is INamedTypeSymbol named)
        {
            foreach (var arg in named.TypeArguments)
            {
                if (TypeReferences(arg, target))
                    return true;
            }
        }
        return false;
    }

    private static bool IsDbContextFactoryOf(
        ITypeSymbol paramType,
        INamedTypeSymbol dbContextFactoryDef,
        INamedTypeSymbol? dbContextType)
    {
        if (paramType is not INamedTypeSymbol named || !named.IsGenericType)
            return false;
        if (!SymbolEqualityComparer.Default.Equals(named.ConstructedFrom, dbContextFactoryDef))
            return false;
        if (dbContextType is null)
            return true;
        // Only complain when the factory is parameterized on HumansDbContext.
        return named.TypeArguments.Length == 1 &&
               TypeReferences(named.TypeArguments[0], dbContextType);
    }

    private static Location FirstLocation(INamedTypeSymbol type) =>
        type.Locations.Length > 0 ? type.Locations[0] : Location.None;
}
