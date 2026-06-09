using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0031 — controllers don't carry business logic. Every method declared in
/// a <c>ControllerBase</c>-derived type (actions <i>and</i> private helpers,
/// so extracting a helper inside the controller doesn't dodge the rule) is
/// measured for statement count and cyclomatic complexity; breaching either
/// hardcoded threshold is an Error.
/// <list type="bullet">
/// <item>Pre-existing offenders carry method-level
/// <c>[Grandfathered("HUM0031", …)]</c> → diagnostic rides as
/// <see cref="DiagnosticSeverity.Warning"/>.</item>
/// <item>A new breach (no <c>[Grandfathered]</c>) →
/// <see cref="DiagnosticSeverity.Error"/>, failing the PR that adds it. Move
/// the decision logic into the section's application service — controllers
/// parse the request, call services, and format/sort/filter the response.</item>
/// </list>
/// The thresholds are deliberately generous: they're calibrated to flag only
/// the worst offenders (~15 methods at introduction) and are ratcheted
/// <b>down</b> over time as offenders are fixed. Replaces the retired
/// <c>NoBusinessLogicInControllersRule</c> ratchet test, whose regex
/// heuristic was noisy and only saw public action signatures
/// (nobodies-collective/Humans#793). Source rule:
/// <c>memory/architecture/no-business-logic-in-controllers.md</c>.
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Web</c> only. Cyclomatic complexity counts real branch
/// nodes in the syntax tree (<c>if</c>/loops/<c>case</c> labels/switch-expression
/// arms/<c>catch</c>/ternary/<c>&amp;&amp;</c>/<c>||</c>/<c>??</c>/<c>??=</c>/
/// <c>and</c>/<c>or</c> patterns), so strings, comments, and nullable
/// annotations can't pollute the count the way the old regex scan did. Local
/// functions count toward their containing method.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ControllerBusinessLogicAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0031";

    /// <summary>Flag when a method body has more statements than this.</summary>
    public const int MaxStatements = 40;

    /// <summary>Flag when a method's cyclomatic complexity exceeds this.</summary>
    public const int MaxCyclomaticComplexity = 15;

    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";

    private static readonly LocalizableString Title =
        "Controller method carries too much logic";

    private static readonly LocalizableString MessageFormat =
        "Controller method '{0}' has {1} statements (max {2}) and cyclomatic complexity {3} (max {4}). " +
        "Move the decision logic into the section's application service — controllers parse the request, " +
        "call services, and format/sort/filter the response. " +
        "See memory/architecture/no-business-logic-in-controllers.md.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Controllers are the display layer: parse, delegate, format. A method this large or this " +
            "branchy is making decisions that belong in an application service. Thresholds are " +
            "hardcoded and ratchet down over time; pre-existing offenders carry " +
            "[Grandfathered(\"HUM0031\", …)] which downgrades to Warning until they are refactored.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Web, System.StringComparison.Ordinal))
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSyntaxNodeAction(
            ctx => AnalyzeMethod(ctx, grandfatheredAttr),
            SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context, INamedTypeSymbol? grandfatheredAttr)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null)
            return;

        if (context.ContainingSymbol is not IMethodSymbol methodSymbol)
            return;

        if (!methodSymbol.ContainingType.InheritsFromOrEquals(ControllerBaseFullName))
            return;

        var statements = CountStatements(body);
        var complexity = ComputeCyclomaticComplexity(body);

        if (statements <= MaxStatements && complexity <= MaxCyclomaticComplexity)
            return;

        var severity = GrandfatheredCheck.EffectiveSeverity(methodSymbol, grandfatheredAttr, DiagnosticId);

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: method.Identifier.GetLocation(),
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs:
            [
                methodSymbol.Name,
                statements,
                MaxStatements,
                complexity,
                MaxCyclomaticComplexity,
            ]));
    }

    private static int CountStatements(SyntaxNode body)
    {
        var count = 0;
        foreach (var node in body.DescendantNodes())
        {
            if (node is StatementSyntax and not BlockSyntax)
                count++;
        }

        // An expression-bodied method has no StatementSyntax at all — count
        // the expression as one statement.
        return body is ArrowExpressionClauseSyntax && count == 0 ? 1 : count;
    }

    private static int ComputeCyclomaticComplexity(SyntaxNode body)
    {
        var complexity = 1;
        foreach (var node in body.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case CommonForEachStatementSyntax:
                case DoStatementSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                case BinaryPatternSyntax:
                    complexity++;
                    break;
                case BinaryExpressionSyntax binary when
                    binary.IsKind(SyntaxKind.LogicalAndExpression)
                    || binary.IsKind(SyntaxKind.LogicalOrExpression)
                    || binary.IsKind(SyntaxKind.CoalesceExpression):
                    complexity++;
                    break;
                case AssignmentExpressionSyntax assignment when
                    assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                    complexity++;
                    break;
            }
        }

        return complexity;
    }
}
