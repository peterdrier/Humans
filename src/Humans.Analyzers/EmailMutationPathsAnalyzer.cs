using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// Pins the single legitimate call chain for the email-mutation primitive:
/// <c>AccountController</c> → <c>IUserEmailService.UpdateEmailAsync</c> →
/// <c>IUserEmailRepository.UpdateEmailAsync</c>. Any other call site is forbidden.
/// </summary>
/// <remarks>
/// See <c>memory/architecture/email-mutation-paths.md</c>. The atom names this
/// analyzer as the build-time enforcement and instructs not to add a parallel
/// IL-scan test.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmailMutationPathsAnalyzer : DiagnosticAnalyzer
{
    public const string ServiceCallerDiagnosticId = "HUM0005";
    public const string RepositoryCallerDiagnosticId = "HUM0006";

    public static readonly DiagnosticDescriptor ServiceCallerRule = new(
        id: ServiceCallerDiagnosticId,
        title: "IUserEmailService.UpdateEmailAsync may only be called from AccountController",
        messageFormat:
            "IUserEmailService.UpdateEmailAsync may only be called from AccountController " +
            "(the OAuth sign-in callback). See memory/architecture/email-mutation-paths.md.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The OAuth callback is the only path that holds the authoritative " +
            "(Provider, ProviderKey, newEmail) triple at the atomic moment Google asserts " +
            "the rename. Every other surface operates on stale state and cannot produce a " +
            "correct rewrite.");

    public static readonly DiagnosticDescriptor RepositoryCallerRule = new(
        id: RepositoryCallerDiagnosticId,
        title: "IUserEmailRepository.UpdateEmailAsync may only be called from UserEmailService",
        messageFormat:
            "IUserEmailRepository.UpdateEmailAsync may only be called from UserEmailService " +
            "(which wraps it with EnsureGoogleInvariantAsync + FullProfile cache invalidation). " +
            "See memory/architecture/email-mutation-paths.md.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The repository primitive is wrapped by the service for invariant maintenance. " +
            "Bypassing the service skips the Google-row reconciliation and cache invalidation.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(ServiceCallerRule, RepositoryCallerRule);

    private const string ServiceInterface = "Humans.Application.Interfaces.Profiles.IUserEmailService";
    private const string RepositoryInterface = "Humans.Application.Interfaces.Repositories.IUserEmailRepository";
    private const string MethodName = "UpdateEmailAsync";
    private const string AllowedServiceCaller = "Humans.Web.Controllers.AccountController";
    private const string AllowedRepositoryCaller = "Humans.Application.Services.Profile.UserEmailService";

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Scope: Application + Web + Infrastructure. The repository implementation lives in
        // Infrastructure; the service / controllers live in Application + Web. The two
        // allowlisted callers themselves are in Web and Application respectively — neither
        // is excluded from the scope, instead the type-name guard below admits them.
        if (!AssemblyScope.IsApplicationWebOrInfrastructure(context.Compilation.Assembly))
            return;

        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var op = (IInvocationOperation)context.Operation;
        var method = op.TargetMethod;
        if (!string.Equals(method.Name, MethodName, System.StringComparison.Ordinal))
            return;

        var callerTopLevel = context.ContainingSymbol.ContainingTopLevelType()?.ToDisplayString();

        if (InterfaceMethodMatcher.Targets(method, ServiceInterface, MethodName))
        {
            if (!string.Equals(callerTopLevel, AllowedServiceCaller, System.StringComparison.Ordinal))
                context.ReportDiagnostic(Diagnostic.Create(ServiceCallerRule, op.Syntax.GetLocation()));
            return;
        }

        if (InterfaceMethodMatcher.Targets(method, RepositoryInterface, MethodName))
        {
            if (!string.Equals(callerTopLevel, AllowedRepositoryCaller, System.StringComparison.Ordinal))
                context.ReportDiagnostic(Diagnostic.Create(RepositoryCallerRule, op.Syntax.GetLocation()));
        }
    }
}
