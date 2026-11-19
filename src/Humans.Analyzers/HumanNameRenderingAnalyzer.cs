using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0041 — a person's name on a page is the <c>&lt;vc:human&gt;</c> link with the profile popup
/// (memory/architecture/burnername-is-the-display-name.md), so a view never writes
/// <c>UserInfo.BurnerName</c> into the page itself. Turning the link off is not possible by accident:
/// <c>HumanLink</c> has no plain "none", only the two values that name why a name has no link.
/// </summary>
/// <remarks>
/// Views are Razor-generated code, so this analyzer opts in to generated code and reports only
/// generated code that maps back to a .cshtml line. Sorting or comparing by the name stays legal.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HumanNameRenderingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0041";

    private const string UserInfoFullName = "Humans.Users.Contracts.UserInfo";

    private static readonly ImmutableHashSet<string> RazorWriteMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Write", "WriteAttributeValue", "AddHtmlAttributeValue");

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Human name rendered without the profile popup",
        messageFormat: "A view writes UserInfo.BurnerName as plain text: render the person with <vc:human> (or a table's Human column), so the name opens the profile popup",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Every person's name links to their profile through the shared Human component. A view writing " +
            "UserInfo.BurnerName skips the component and leaves the name dead text.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var op = (IPropertyReferenceOperation)context.Operation;
        if (!context.IsGeneratedCode
            || !string.Equals(op.Property.Name, "BurnerName", StringComparison.Ordinal)
            || !string.Equals(op.Property.ContainingType?.ToDisplayString(), UserInfoFullName, StringComparison.Ordinal))
            return;

        // Generated and unmapped = a component's own internals, not a hand-written view line.
        var location = op.Syntax.GetLocation();
        if (!FlowsIntoRazorWrite(op) || !location.GetMappedLineSpan().HasMappedPath)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, location));
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
}
