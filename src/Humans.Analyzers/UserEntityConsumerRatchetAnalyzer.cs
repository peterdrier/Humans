using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0039 — the <c>User</c> and <c>UserEmail</c> EF entities sit in Humans.Base so Base needs no project reference,
/// which also lets every assembly name them without declaring a dependency on Users. Only Users (and Base) may name
/// them; the assemblies that already did when they moved are a baseline that only shrinks (CENTRAL-106). A baselined
/// assembly that no longer names either entity is itself an error, so its entry gets deleted.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UserEntityConsumerRatchetAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0039";
    public const string StaleBaselineDiagnosticId = "HUM0040";

    private static readonly ImmutableHashSet<string> EntityFullNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Humans.Users.Contracts.User",
        "Humans.Users.Contracts.UserEmail");

    private static readonly ImmutableHashSet<string> Owners = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Humans.Base",
        "Humans.Users",
        "Humans.Users.Contracts");

    // Remove entries as each assembly moves onto UserInfo / IUserService. Never add one.
    internal static readonly ImmutableHashSet<string> Baseline = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Humans.Auth",
        "Humans.Development",
        "Humans.Tickets",
        "Humans.Web");

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "User / UserEmail entity named outside the Users section",
        messageFormat: "'{0}' is the Users section's EF entity; read UserInfo / IUserService instead (CENTRAL-106)",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "EF entities stay inside their section. User and UserEmail live in Humans.Base only so Base needs no " +
            "project reference; outside Users, only the assemblies in this analyzer's baseline may still name them.");

    public static readonly DiagnosticDescriptor StaleBaselineRule = new(
        id: StaleBaselineDiagnosticId,
        title: "HUM0039 baseline entry is stale",
        messageFormat: "Assembly '{0}' no longer names User or UserEmail; delete it from the HUM0039 baseline",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The HUM0039 baseline only shrinks: an entry whose assembly no longer needs it is removed.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule, StaleBaselineRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var assemblyName = context.Compilation.AssemblyName ?? string.Empty;
        if (Owners.Contains(assemblyName))
            return;

        var baselined = Baseline.Contains(assemblyName);
        var named = 0;

        context.RegisterSyntaxNodeAction(
            ctx =>
            {
                var name = (SimpleNameSyntax)ctx.Node;
                if (!IsEntityName(name.Identifier.ValueText) || name.FirstAncestorOrSelf<CrefSyntax>() is not null)
                    return;

                if (ctx.SemanticModel.GetSymbolInfo(name, ctx.CancellationToken).Symbol is not INamedTypeSymbol type
                    || !EntityFullNames.Contains(type.OriginalDefinition.ToDisplayString()))
                    return;

                if (baselined)
                {
                    Interlocked.Exchange(ref named, 1);
                    return;
                }

                ctx.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation(), type.Name));
            },
            SyntaxKind.IdentifierName);

        if (baselined)
        {
            context.RegisterCompilationEndAction(ctx =>
            {
                if (Volatile.Read(ref named) == 0)
                    ctx.ReportDiagnostic(Diagnostic.Create(StaleBaselineRule, Location.None, assemblyName));
            });
        }
    }

    private static bool IsEntityName(string identifier) =>
        string.Equals(identifier, "User", StringComparison.Ordinal)
        || string.Equals(identifier, "UserEmail", StringComparison.Ordinal);
}
