using System;
using System.Collections.Immutable;
using System.Linq;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0007 -- live source must not add EF or data-annotation concurrency tokens.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConcurrencyTokenAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0007";

    private static readonly LocalizableString Title =
        "Concurrency tokens are forbidden";

    private static readonly LocalizableString MessageFormat =
        "Concurrency token '{0}' is forbidden in live source. The audit log is the safety net; do not add row-versioning or concurrency-token metadata.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Humans does not use EF concurrency tokens, row versions, [ConcurrencyCheck], or [Timestamp] " +
            "in live source. Historical EF migration snapshots are excluded.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    private static readonly ImmutableHashSet<string> ProductionAssemblies =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Humans.Application",
            "Humans.Domain",
            "Humans.Infrastructure",
            "Humans.Web");

    private static readonly ImmutableHashSet<string> ForbiddenEfMethods =
        ImmutableHashSet.Create(StringComparer.Ordinal, "IsConcurrencyToken", "IsRowVersion");

    private static readonly ImmutableHashSet<string> ForbiddenAttributeFullNames =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "System.ComponentModel.DataAnnotations.ConcurrencyCheckAttribute",
            "System.ComponentModel.DataAnnotations.TimestampAttribute");

    private const string EfBuilderNamespacePrefix = "Microsoft.EntityFrameworkCore.Metadata.Builders";
    private const string MigrationsNamespacePrefix = "Humans.Infrastructure.Migrations";

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!ProductionAssemblies.Contains(context.Compilation.Assembly.Name))
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterOperationAction(
            ctx => AnalyzeInvocation(ctx, grandfatheredAttr),
            OperationKind.Invocation);
        context.RegisterSymbolAction(
            ctx => AnalyzeAttributedSymbol(ctx, grandfatheredAttr),
            SymbolKind.Field, SymbolKind.Property);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol? grandfatheredAttr)
    {
        if (IsInMigration(context.ContainingSymbol, context.Operation.Syntax.SyntaxTree.FilePath))
            return;

        var op = (IInvocationOperation)context.Operation;
        var method = op.TargetMethod;
        if (!ForbiddenEfMethods.Contains(method.Name))
            return;

        var containingNamespace = method.ContainingNamespace?.ToDisplayString();
        if (containingNamespace is null ||
            !containingNamespace.StartsWith(EfBuilderNamespacePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var severity = EffectiveSeverity(context.ContainingSymbol?.ContainingType, grandfatheredAttr);

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: op.Syntax.GetLocation(),
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: method.Name));
    }

    private static void AnalyzeAttributedSymbol(SymbolAnalysisContext context, INamedTypeSymbol? grandfatheredAttr)
    {
        if (IsInMigration(context.Symbol, context.Symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath))
            return;

        foreach (var attr in context.Symbol.GetAttributes())
        {
            var attrType = attr.AttributeClass;
            if (attrType is null ||
                !ForbiddenAttributeFullNames.Contains(attrType.ToDisplayString()))
            {
                continue;
            }

            var severity = EffectiveSeverity(context.Symbol.ContainingType, grandfatheredAttr);

            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: Rule,
                location: PreferAttributeLocation(attr, context.Symbol),
                effectiveSeverity: severity,
                additionalLocations: null,
                properties: null,
                messageArgs: attrType.Name));
        }
    }

    private static DiagnosticSeverity EffectiveSeverity(INamedTypeSymbol? containingType, INamedTypeSymbol? grandfatheredAttr) =>
        containingType is not null
            ? GrandfatheredCheck.EffectiveSeverity(containingType, grandfatheredAttr, DiagnosticId)
            : DiagnosticSeverity.Error;

    private static bool IsInMigration(ISymbol? symbol, string? filePath)
    {
        var ns = symbol?.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns.StartsWith(MigrationsNamespacePrefix, StringComparison.Ordinal))
            return true;

        if (filePath is null || filePath.Length == 0)
            return false;

        var normalized = filePath.Replace('\\', '/');
        return normalized.Contains("/Humans.Infrastructure/Migrations/", StringComparison.Ordinal);
    }

    private static Location? PreferAttributeLocation(AttributeData attr, ISymbol fallback)
    {
        var syntaxRef = attr.ApplicationSyntaxReference;
        if (syntaxRef is not null)
            return syntaxRef.GetSyntax().GetLocation();

        return fallback.Locations.FirstOrDefault();
    }
}
