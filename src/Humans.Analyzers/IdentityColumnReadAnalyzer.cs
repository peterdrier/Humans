using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0019 — application/web code must not READ the four Identity-derived
/// columns on <c>User</c> (<c>Email</c>, <c>NormalizedEmail</c>,
/// <c>UserName</c>, <c>NormalizedUserName</c>). Read paths go through
/// <c>UserInfo.Email</c> / <c>UserInfo.PrimaryEmail</c> /
/// <c>IUserEmailService.GetPrimaryEmailAsync</c> /
/// <c>IUserEmailRepository</c> instead. Pairs with HUM0002 (writes) and
/// HUM0003 (FindByEmailAsync/FindByNameAsync).
///
/// Issue nobodies-collective/Humans#506.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdentityColumnReadAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0019";

    private static readonly LocalizableString Title =
        "Identity-derived column on User must not be read from Application or Web";

    private static readonly LocalizableString MessageFormat =
        "Read of 'User.{0}' from application or web code. " +
        "The Identity-derived columns (Email / NormalizedEmail / UserName / " +
        "NormalizedUserName) are virtual overrides that compute from " +
        "UserEmails or Id; reading them couples application code to a " +
        "vestigial Identity field. Use UserInfo.Email / UserInfo.PrimaryEmail " +
        "via IUserService, or IUserEmailService / IUserEmailRepository for " +
        "direct UserEmail-backed lookups.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Issue nobodies-collective/Humans#506 — stops application/web from " +
            "reading the four Identity-derived columns on User. Use UserInfo " +
            "or IUserEmailService instead.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    private static readonly ImmutableHashSet<string> ForbiddenGetters =
        ImmutableHashSet.Create(System.StringComparer.Ordinal,
            "Email",
            "NormalizedEmail",
            "UserName",
            "NormalizedUserName");

    // String built from segments to keep architecture scans that operate on
    // source text from confusing this metadata-name constant with a User nav.
    private const string UserFullName = "Humans.Domain.Entities" + "." + "User";

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

        // PropertyReference fires for direct reads (`user.Email`), reads inside
        // property patterns (`is { Email: ... }`), and reads inside switch
        // arm patterns. Subpattern operations always wrap a PropertyReference
        // child, so registering only PropertyReference catches every read
        // path without double-counting.
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var op = (IPropertyReferenceOperation)context.Operation;
        var prop = op.Property;
        if (!ForbiddenGetters.Contains(prop.Name))
            return;

        var declaring = prop.ContainingType;
        if (declaring is null)
            return;

        // Match concrete User and any class deriving from IdentityUser.
        if (!declaring.InheritsFromOrEquals(UserFullName) && !declaring.NameStartsWith("IdentityUser"))
            return;

        // Skip writes — those are HUM0002's job. Detect by walking the parent
        // chain: a SimpleAssignment / CompoundAssignment / CoalesceAssignment
        // whose Target is this operation is a write, not a read.
        if (IsAssignmentTarget(op))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, op.Syntax.GetLocation(), prop.Name));
    }

    private static bool IsAssignmentTarget(IOperation op)
    {
        var parent = op.Parent;
        return parent switch
        {
            ISimpleAssignmentOperation sa => ReferenceEquals(sa.Target, op),
            ICompoundAssignmentOperation ca => ReferenceEquals(ca.Target, op),
            ICoalesceAssignmentOperation coa => ReferenceEquals(coa.Target, op),
            _ => false
        };
    }
}
