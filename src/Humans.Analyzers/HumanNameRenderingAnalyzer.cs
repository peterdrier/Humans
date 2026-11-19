using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0041 — a person's name on a page is the <c>&lt;vc:human&gt;</c> link with the profile popup
/// (memory/architecture/burnername-is-the-display-name.md). Two ways around it are caught: passing
/// <c>HumanLink.None</c>, which renders the name as dead text, and a view writing
/// <c>UserInfo.BurnerName</c> straight into the page.
/// </summary>
/// <remarks>
/// Views are Razor-generated code, so this analyzer opts in to generated code and reports only
/// generated code that maps back to a .cshtml line. A deliberate exception (the popup's own body,
/// the viewer's own name, an anonymous page) carries <c>#pragma warning disable HUM0041</c> with
/// its reason.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HumanNameRenderingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0041";

    private const string HumanLinkFullName = "Humans.Base.ViewComponents.HumanLink";
    private const string UserInfoFullName = "Humans.Users.Contracts.UserInfo";

    private static readonly ImmutableHashSet<string> RazorWriteMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Write", "WriteAttributeValue", "AddHtmlAttributeValue");

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Human name rendered without the profile popup",
        messageFormat: "{0}: render the person with <vc:human> (or a table's Human column) and keep its link, so the name opens the profile popup",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Every person's name links to their profile through the shared Human component. HumanLink.None turns " +
            "the name into dead text, and a view writing UserInfo.BurnerName skips the component. Deliberate " +
            "exceptions suppress with #pragma and a reason.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeFieldReference, OperationKind.FieldReference);
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
    }

    private static void AnalyzeFieldReference(OperationAnalysisContext context)
    {
        var op = (IFieldReferenceOperation)context.Operation;
        if (!string.Equals(op.Field.Name, "None", StringComparison.Ordinal)
            || !string.Equals(op.Field.ContainingType?.ToDisplayString(), HumanLinkFullName, StringComparison.Ordinal))
            return;

        // Comparing against None (the component's own branching) renders nothing.
        if (SkipConversions(op.Parent) is IBinaryOperation or IConstantPatternOperation or ISingleValueCaseClauseOperation)
            return;

        if (!TryGetReportLocation(context, op, out var location))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, location, "HumanLink.None renders the name without its link"));
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var op = (IPropertyReferenceOperation)context.Operation;
        if (!context.IsGeneratedCode
            || !string.Equals(op.Property.Name, "BurnerName", StringComparison.Ordinal)
            || !string.Equals(op.Property.ContainingType?.ToDisplayString(), UserInfoFullName, StringComparison.Ordinal))
            return;

        if (!FlowsIntoRazorWrite(op) || !TryGetReportLocation(context, op, out var location))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, location, "A view writes UserInfo.BurnerName as plain text"));
    }

    // Walks up through the shapes a name takes on its way into the page: conversions, ?., ??,
    // ?: and string interpolation. Sorting and comparing a name stays legal.
    private static bool FlowsIntoRazorWrite(IOperation op)
    {
        var current = op;
        while (current.Parent is { } parent)
        {
            switch (parent)
            {
                case IConversionOperation:
                case IConditionalAccessOperation:
                case ICoalesceOperation:
                case IConditionalOperation c when !ReferenceEquals(c.Condition, current):
                case IInterpolationOperation:
                case IInterpolatedStringOperation:
                    current = parent;
                    continue;
                case IArgumentOperation { Parent: IInvocationOperation invocation }:
                    return RazorWriteMethods.Contains(invocation.TargetMethod.Name);
                default:
                    return false;
            }
        }
        return false;
    }

    private static IOperation? SkipConversions(IOperation? op)
    {
        while (op is IConversionOperation)
            op = op.Parent;
        return op;
    }

    // Generated and unmapped = a component's own internals, not a hand-written view line. Razor writes a
    // tag-helper enum value as an unmapped type prefix plus the mapped member name, so locate by the name.
    private static bool TryGetReportLocation(OperationAnalysisContext context, IOperation op, out Location location)
    {
        location = op.Syntax is MemberAccessExpressionSyntax access
            ? access.Name.GetLocation()
            : op.Syntax.GetLocation();
        return !context.IsGeneratedCode || location.GetMappedLineSpan().HasMappedPath;
    }
}
