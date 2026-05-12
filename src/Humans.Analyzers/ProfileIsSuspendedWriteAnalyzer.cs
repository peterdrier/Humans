using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProfileIsSuspendedWriteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0004";

    private static readonly LocalizableString Title =
        "Profile.IsSuspended must not be written outside the allowlisted dual-writers";

    private static readonly LocalizableString MessageFormat =
        "Profile.IsSuspended is [Obsolete]. New writers must mutate " +
        "Profile.State (= ProfileState.Suspended) instead. The only sites permitted " +
        "to dual-write IsSuspended + State until the legacy column is dropped are " +
        "ProfileService and ProfileRepository (Issue #635 §15i).";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Issue #635 (§15i) makes Profile.State the canonical lifecycle marker. " +
            "Profile.IsSuspended is dual-written by ProfileService / ProfileRepository " +
            "until the lazy-State-backfill follow-up drops the column.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private const string ProfileFullName = "Humans.Domain.Entities.Profile";
    private const string IsSuspendedPropertyName = "IsSuspended";

    private static readonly ImmutableHashSet<string> AllowedWriterTypes =
        ImmutableHashSet.Create(System.StringComparer.Ordinal,
            "Humans.Application.Services.Profile.ProfileService",
            "Humans.Infrastructure.Repositories.Profiles.ProfileRepository");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Scope: Application + Web + Infrastructure (the original IL test scanned all three;
        // the allowlist lives in Application + Infrastructure).
        if (!AssemblyScope.IsApplicationWebOrInfrastructure(context.Compilation.Assembly))
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
        if (!string.Equals(prop.Name, IsSuspendedPropertyName, System.StringComparison.Ordinal))
            return;

        var declaring = prop.ContainingType;
        if (declaring is null || !declaring.InheritsFromOrEquals(ProfileFullName))
            return;

        var callerTopLevel = context.ContainingSymbol.ContainingTopLevelType();
        if (callerTopLevel is not null)
        {
            var callerFullName = callerTopLevel.ToDisplayString();
            if (AllowedWriterTypes.Contains(callerFullName))
                return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation()));
    }
}
