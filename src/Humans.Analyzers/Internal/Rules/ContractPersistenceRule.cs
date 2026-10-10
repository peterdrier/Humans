using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers.Internal.Rules;

/// <summary>Contract code cannot expose or use persistence infrastructure.</summary>
internal static class ContractPersistenceRule
{
    public const string DiagnosticId = "HUM0037";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Persistence type used in Contracts",
        messageFormat: "'{0}' is a persistence type. Keep it inside the section's Data layer, not its contracts.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Contracts expose materialized domain projections, never EF infrastructure or IQueryable. Identity user models remain valid contract types.");

    public static void Register(CompilationStartAnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (!IsContractCode(context))
            return;

        if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is not INamedTypeSymbol type)
            return;

        if (type.AllInterfaces.Any(IsQueryable))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), type.ToDisplayString()));
            return;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            var ns = current.ContainingNamespace?.ToDisplayString();
            if (IsNamespace(ns, "Microsoft.EntityFrameworkCore")
                || IsNamespace(ns, "Microsoft.AspNetCore.Identity.EntityFrameworkCore")
                || IsQueryable(current))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), type.ToDisplayString()));
                return;
            }
        }
    }

    private static bool IsContractCode(SyntaxNodeAnalysisContext context)
    {
        if (context.Compilation.AssemblyName?.EndsWith(".Contracts", StringComparison.Ordinal) == true)
            return true;

        var path = "/" + context.Node.SyntaxTree.FilePath.Replace('\\', '/').TrimStart('/') + "/";
        if (path.IndexOf("/Contracts/", StringComparison.Ordinal) >= 0)
            return true;

        var symbol = context.SemanticModel.GetEnclosingSymbol(context.Node.SpanStart, context.CancellationToken);
        var owner = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        return owner is not null && PublicSurfaceRule.IsUnderContracts(owner);
    }

    private static bool IsNamespace(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.Ordinal)
        || actual?.StartsWith(expected + ".", StringComparison.Ordinal) == true;

    private static bool IsQueryable(INamedTypeSymbol type) =>
        string.Equals(type.ContainingNamespace?.ToDisplayString(), "System.Linq", StringComparison.Ordinal)
        && string.Equals(type.Name, "IQueryable", StringComparison.Ordinal);
}
