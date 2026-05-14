using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SurfaceBudgetAnalyzer : DiagnosticAnalyzer
{
    public const string OverBudgetDiagnosticId = "HUM0015";
    public const string SlackDiagnosticId = "HUM0016";

    private const string SurfaceBudgetAttributeFullName =
        "Humans.Application.Architecture.SurfaceBudgetAttribute";

    private static readonly LocalizableString OverBudgetTitle =
        "Type surface exceeds budget";

    private static readonly LocalizableString OverBudgetMessageFormat =
        "'{0}' has {1} public instance methods, budget is {2}. Remove a method " +
        "in this PR (preferred — decrement the budget to match), or raise the " +
        "budget only with explicit owner authorization. Run /audit-surface {0} " +
        "to see what could be eliminated.";

    private static readonly LocalizableString OverBudgetDescription =
        "[SurfaceBudget(N)] is a consolidation ratchet — budgeted types " +
        "should shrink over time. Net delta from any PR is <= 0. Only the " +
        "repo owner authorizes raises, and only out-of-band.";

    public static readonly DiagnosticDescriptor OverBudgetRule = new(
        id: OverBudgetDiagnosticId,
        title: OverBudgetTitle,
        messageFormat: OverBudgetMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: OverBudgetDescription);

    private static readonly LocalizableString SlackTitle =
        "Surface budget has slack";

    private static readonly LocalizableString SlackMessageFormat =
        "'{0}' budget ({2}) should equal current count ({1}). When you remove " +
        "a method, decrement the budget — the ratchet works only when there's " +
        "no headroom.";

    private static readonly LocalizableString SlackDescription =
        "[SurfaceBudget(N)] budgets must be tight. Slack absorbs the next " +
        "addition with no friction, defeating the consolidation goal.";

    public static readonly DiagnosticDescriptor SlackRule = new(
        id: SlackDiagnosticId,
        title: SlackTitle,
        messageFormat: SlackMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: SlackDescription);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(OverBudgetRule, SlackRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var attributeType = context.Compilation.GetTypeByMetadataName(SurfaceBudgetAttributeFullName);
        if (attributeType is null)
            return;

        context.RegisterSymbolAction(c => AnalyzeNamedType(c, attributeType), SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol attributeType)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind is not (TypeKind.Interface or TypeKind.Class or TypeKind.Struct))
            return;

        var budgetAttribute = type.GetAttributes().FirstOrDefault(a =>
            SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));
        if (budgetAttribute is null)
            return;

        if (budgetAttribute.ConstructorArguments.Length < 1)
            return;

        var budgetArg = budgetAttribute.ConstructorArguments[0];
        if (budgetArg.Value is not int budget)
            return;

        var count = CountDirectlyDeclaredPublicInstanceOrdinaryMethods(type);
        var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;

        if (count > budget)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                OverBudgetRule, location, type.Name, count, budget));
        }
        else if (count < budget)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SlackRule, location, type.Name, count, budget));
        }
    }

    /// <summary>
    /// Counts directly-declared <b>public instance</b> ordinary methods on the
    /// type. Private/internal/protected and static methods are not counted;
    /// property accessors, indexers, events, and inherited members are not
    /// counted.
    ///
    /// <para>
    /// <c>INamedTypeSymbol.GetMembers()</c> returns only the members declared
    /// directly on this symbol (analog of <c>BindingFlags.DeclaredOnly</c>).
    /// Property/indexer/event accessors surface as <c>IMethodSymbol</c> with
    /// a non-<c>Ordinary</c> <see cref="MethodKind"/>, so filtering on
    /// <c>MethodKind == Ordinary</c> is the analog of <c>!IsSpecialName</c>.
    /// For interfaces, every declared method is implicitly public-instance so
    /// the accessibility filter is a no-op there.
    /// </para>
    /// </summary>
    private static int CountDirectlyDeclaredPublicInstanceOrdinaryMethods(INamedTypeSymbol type)
    {
        var count = 0;
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;
            if (method.MethodKind != MethodKind.Ordinary)
                continue;
            if (method.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (method.IsStatic)
                continue;
            if (method.IsImplicitlyDeclared)
                continue;
            count++;
        }
        return count;
    }
}
