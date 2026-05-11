using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdentityFindByEmailAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0003";

    private static readonly LocalizableString Title =
        "UserManager.FindByEmailAsync / FindByNameAsync must not be called from Application or Web";

    private static readonly LocalizableString MessageFormat =
        "UserManager.{0} queries the AspNetUsers.Email / NormalizedEmail / " +
        "NormalizedUserName columns directly; those columns are no longer populated " +
        "post-PR 1 of the email-identity-decoupling spec and silently return null. " +
        "Use IUserEmailService.FindVerifiedEmailWithUserAsync " +
        "(or IMagicLinkService.FindUserByVerifiedEmailAsync) for email lookups; " +
        "FindByIdAsync for id lookups.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "PR 2 of the email-identity-decoupling spec stops populating the " +
            "AspNetUsers.Email / NormalizedEmail / NormalizedUserName columns. " +
            "Identity's FindByEmailAsync / FindByNameAsync silently return null " +
            "for users created post-PR 1.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private static readonly ImmutableHashSet<string> ForbiddenMethods =
        ImmutableHashSet.Create(System.StringComparer.Ordinal,
            "FindByEmailAsync",
            "FindByNameAsync");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!AssemblyScope.IsApplicationOrWeb(context.Compilation.Assembly))
            return;

        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var op = (IInvocationOperation)context.Operation;
        var method = op.TargetMethod;
        if (!ForbiddenMethods.Contains(method.Name))
            return;

        // Receiver type chain — match UserManager / UserManager<T> by simple name
        // prefix to avoid pulling in Microsoft.AspNetCore.Identity transitively.
        if (!method.ContainingType.NameStartsWith("UserManager"))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, op.Syntax.GetLocation(), method.Name));
    }
}
