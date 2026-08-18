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
/// <item><term>HUM0008</term><description>A controller never injects a
/// DbContext.</description></item>
/// <item><term>HUM0009</term><description>Only a repository uses a
/// DbContext.</description></item>
/// <item><term>HUM0025</term><description>A table belongs to exactly one
/// repository.</description></item>
/// <item><term>HUM0020</term><description>A caching decorator calls the inner service, not
/// the repository.</description></item>
/// <item><term>HUM0032</term><description>Cross-section injection takes the read
/// interface unless the class is marked [CrossSectionWrite].</description></item>
/// </list>
///
/// The first two are about a section's shape and self-gate on its entry point. The rest
/// hold everywhere our code touches persistence, including what is left in Base.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SectionRulesAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        PublicSurfaceRule.Rule,
        RepositoryPlacementRule.Rule,
        ControllerDbContextRule.Rule,
        DbContextOwnershipRule.Rule,
        TableOwnershipRule.Rule,
        CachingDecoratorRule.Rule,
        CrossSectionReadRule.Rule,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        ControllerDbContextRule.Register(context);
        DbContextOwnershipRule.Register(context);
        TableOwnershipRule.Register(context);
        CachingDecoratorRule.Register(context);
        CrossSectionReadRule.Register(context);

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
