using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0031 — controllers don't carry business logic. Every method in a <c>ControllerBase</c>-derived type, helpers
/// too (so extracting one doesn't dodge the rule), is measured for statement count and cyclomatic complexity.
/// <c>[Grandfathered("HUM0031", …)]</c> on the method downgrades to a Warning. The 40/15 thresholds are a fixed
/// backstop and never ratchet down: whether a smaller method carries business logic is judged by section-doctor.
/// </summary>
/// <remarks>
/// Complexity counts real branch nodes in the syntax tree, so strings, comments and nullable annotations can't
/// pollute it. Local functions count toward their containing method.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ControllerBusinessLogicAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0031";

    public const int MaxStatements = 40;

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
            "hardcoded and fixed at 40/15 as a backstop, never ratcheted; a grandfathered method carries " +
            "[Grandfathered(\"HUM0031\", …)] which downgrades to Warning. Below the thresholds, " +
            "controller intent is judged by section-doctor, not by this analyzer.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
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
