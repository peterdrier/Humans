using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Humans.Analyzers.Internal.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// What a section must satisfy, in one place. Every rule below is enforced by this one
/// analyzer; the checks live in <c>Internal/Rules/</c>.
///
/// <list type="table">
/// <item><term>HUM0034</term><description>Outside <c>Contracts/</c> a section's types are
/// internal, except what the framework silently drops when they are not.</description></item>
/// <item><term>HUM0035</term><description>A repository never lives under
/// <c>Contracts/</c> — persistence is not a section's public surface.</description></item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SectionRulesAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        PublicSurfaceRule.Rule,
        RepositoryPlacementRule.Rule,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Both checks are about a section's shape, so they self-gate on the section entry
        // point rather than on an assembly name.
        if (!AssemblyScope.IsSection(context.Compilation.Assembly))
            return;

        var surface = PublicSurfaceRule.Prepare(context.Compilation);
        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx =>
            {
                PublicSurfaceRule.Analyze(ctx, surface, grandfatheredAttr);
                RepositoryPlacementRule.Analyze(ctx, surface.RepositoryMarker, grandfatheredAttr);
            },
            SymbolKind.NamedType);
    }
}
