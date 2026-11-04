using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0039 — the <c>User</c> and <c>UserEmail</c> EF entities sit in Humans.Base so Base needs no project reference,
/// which also lets every assembly name them without declaring a dependency on Users. Only Users (and Base) may name
/// them; everyone else reads <c>UserInfo</c> / <c>IUserService</c> and signs in through <c>IUserSignInService</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UserEntityConsumerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0039";

    private static readonly ImmutableHashSet<string> EntityFullNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Humans.Users.Contracts.User",
        "Humans.Users.Contracts.UserEmail");

    private static readonly ImmutableHashSet<string> Owners = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Humans.Base",
        "Humans.Users",
        "Humans.Users.Contracts");

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "User / UserEmail entity named outside the Users section",
        messageFormat: "'{0}' is the Users section's EF entity; read UserInfo / IUserService, or sign in through IUserSignInService",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "EF entities stay inside their section. User and UserEmail live in Humans.Base only so Base needs no " +
            "project reference; no assembly outside Users may name them.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (Owners.Contains(context.Compilation.AssemblyName ?? string.Empty))
            return;

        context.RegisterSyntaxNodeAction(
            ctx =>
            {
                var name = (SimpleNameSyntax)ctx.Node;
                if (!IsEntityName(name.Identifier.ValueText) || name.FirstAncestorOrSelf<CrefSyntax>() is not null)
                    return;

                if (ctx.SemanticModel.GetSymbolInfo(name, ctx.CancellationToken).Symbol is INamedTypeSymbol type
                    && EntityFullNames.Contains(type.OriginalDefinition.ToDisplayString()))
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation(), type.Name));
            },
            SyntaxKind.IdentifierName);
    }

    private static bool IsEntityName(string identifier) =>
        string.Equals(identifier, "User", StringComparison.Ordinal)
        || string.Equals(identifier, "UserEmail", StringComparison.Ordinal);
}
