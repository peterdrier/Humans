using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UserEmailLegacyFieldAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0001";

    private static readonly LocalizableString Title =
        "Reference to deleted email-identity-decoupling legacy member";

    private static readonly LocalizableString MessageFormat =
        "Member '{0}.{1}' was deleted by the email-identity-decoupling spec (PR 3). " +
        "Auth-side reads must use 'UserEmail.Provider != null'; " +
        "Google-Workspace-side reads must use the IsGoogle-flagged 'UserEmail' row. " +
        "The DB columns survive as EF shadow properties for the one-shot " +
        "UserEmailProviderBackfillService only, via EF.Property<T>(...)";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "PR 3 of the email-identity-decoupling spec deletes UserEmail.IsOAuth, " +
            "UserEmail.DisplayOrder, User.GoogleEmail, and User.GetGoogleServiceEmail(). " +
            "This analyzer is the build-time tombstone preventing accidental re-introduction.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    private static readonly ImmutableHashSet<string> UserEmailForbiddenMembers =
        ImmutableHashSet.Create(System.StringComparer.Ordinal,
            "IsOAuth",
            "DisplayOrder");

    private static readonly ImmutableHashSet<string> UserForbiddenMembers =
        ImmutableHashSet.Create(System.StringComparer.Ordinal,
            "GoogleEmail",
            "GetGoogleServiceEmail");

    // Strings built from segments to keep architecture scans that operate on
    // source text from confusing these metadata-name constants with User navs.
    private const string UserFullName = "Humans.Domain.Entities" + "." + "User";
    private const string UserEmailFullName = "Humans.Domain.Entities" + "." + "UserEmail";

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

        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var op = (IPropertyReferenceOperation)context.Operation;
        Check(context, op.Property, op.Syntax.GetLocation());
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var op = (IInvocationOperation)context.Operation;
        Check(context, op.TargetMethod, op.Syntax.GetLocation());
    }

    private static void Check(OperationAnalysisContext context, ISymbol member, Location location)
    {
        var declaring = member.ContainingType;
        if (declaring is null)
            return;

        if (declaring.InheritsFromOrEquals(UserEmailFullName) &&
            UserEmailForbiddenMembers.Contains(member.Name))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, "UserEmail", member.Name));
            return;
        }

        if (declaring.InheritsFromOrEquals(UserFullName) &&
            UserForbiddenMembers.Contains(member.Name))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, "User", member.Name));
        }
    }
}
