using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// Pins the single legitimate call chain for the OAuth reconcile primitive:
/// <c>AccountController</c> → <c>IUserEmailService.ReconcileOAuthIdentityAsync</c> →
/// <c>IUserEmailRepository.ApplyReconcilePlanAsync</c>. Any other call site is forbidden.
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
        title: "IUserEmailService.ReconcileOAuthIdentityAsync may only be called from AccountController",
        messageFormat:
            "IUserEmailService.ReconcileOAuthIdentityAsync may only be called from AccountController " +
            "(the OAuth sign-in callback). See memory/architecture/email-mutation-paths.md.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The OAuth callback is the only path that holds the authoritative (Provider, " +
            "ProviderKey, email, email_verified) tuple at the moment the IdP asserts it. " +
            "Every other surface operates on stale state and cannot produce a correct reconcile.");

    public static readonly DiagnosticDescriptor RepositoryCallerRule = new(
        id: RepositoryCallerDiagnosticId,
        title: "IUserEmailRepository.ApplyReconcilePlanAsync may only be called from UserEmailService",
        messageFormat:
            "IUserEmailRepository.ApplyReconcilePlanAsync may only be called from UserEmailService " +
            "(which composes the reconcile plan, enforces invariants, and writes audit rows). " +
            "See memory/architecture/email-mutation-paths.md.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The repository primitive is the atomic apply step; the service owns plan " +
            "composition, invariant enforcement, and audit writes. Bypassing the service " +
            "skips all three.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(ServiceCallerRule, RepositoryCallerRule);

    private const string ServiceInterface = "Humans.Application.Interfaces.Profiles.IUserEmailService";
    private const string RepositoryInterface = "Humans.Application.Interfaces.Repositories.IUserEmailRepository";
    private const string ServiceMethodName = "ReconcileOAuthIdentityAsync";
    private const string RepositoryMethodName = "ApplyReconcilePlanAsync";
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
        var name = method.Name;
        if (!string.Equals(name, ServiceMethodName, System.StringComparison.Ordinal) &&
            !string.Equals(name, RepositoryMethodName, System.StringComparison.Ordinal))
            return;

        var callerTopLevel = context.ContainingSymbol.ContainingTopLevelType()?.ToDisplayString();

        if (InterfaceMethodMatcher.Targets(method, ServiceInterface, ServiceMethodName))
        {
            if (!string.Equals(callerTopLevel, AllowedServiceCaller, System.StringComparison.Ordinal))
                context.ReportDiagnostic(Diagnostic.Create(ServiceCallerRule, op.Syntax.GetLocation()));
            return;
        }

        if (InterfaceMethodMatcher.Targets(method, RepositoryInterface, RepositoryMethodName))
        {
            if (!string.Equals(callerTopLevel, AllowedRepositoryCaller, System.StringComparison.Ordinal))
                context.ReportDiagnostic(Diagnostic.Create(RepositoryCallerRule, op.Syntax.GetLocation()));
        }
    }
}
