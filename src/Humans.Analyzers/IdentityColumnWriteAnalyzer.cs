using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdentityColumnWriteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0002";

    private static readonly LocalizableString Title =
        "Identity column on User must not be written from Application or Web";

    private static readonly LocalizableString MessageFormat =
        "Property 'User.{0}' must not be written from Application or Web. " +
        "The Identity-derived columns (Email / NormalizedEmail / EmailConfirmed / " +
        "UserName / NormalizedUserName) are virtual overrides that compute from " +
        "UserEmails or User.Id. Create UserEmail rows via IUserEmailService for " +
        "emails; UserName / NormalizedUserName are derived from Id.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "PR 2 of the email-identity-decoupling spec routes User.Email / NormalizedEmail / " +
            "EmailConfirmed / UserName / NormalizedUserName through virtual overrides on User " +
            "that compute from UserEmails / Id. Writes from Application/Web are stale or " +
            "throw NotSupportedException at runtime.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private static readonly ImmutableHashSet<string> ForbiddenSetters =
        ImmutableHashSet.Create(System.StringComparer.Ordinal,
            "Email",
            "NormalizedEmail",
            "EmailConfirmed",
            "UserName",
            "NormalizedUserName");

    // String built from segments so the NoObsoleteNavReads ratchet (which scans
    // source text for dot-prefixed obsolete-nav names) doesn't false-positive on
    // this metadata-name constant.
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

        context.RegisterOperationAction(
            AnalyzeAssignment,
            OperationKind.SimpleAssignment,
            OperationKind.CompoundAssignment,
            OperationKind.CoalesceAssignment);
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var target = AssignmentTarget.From(context.Operation);
        if (target is not IPropertyReferenceOperation propRef)
            return;

        var prop = propRef.Property;
        if (!ForbiddenSetters.Contains(prop.Name))
            return;

        var declaring = prop.ContainingType;
        if (declaring is null)
            return;

        // Match concrete User and any class deriving from IdentityUser (the
        // Identity base also exposes these setters with the same names).
        if (!declaring.InheritsFromOrEquals(UserFullName) && !declaring.NameStartsWith("IdentityUser"))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), prop.Name));
    }
}
