using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0012 — Every concrete class that implements
/// <c>Humans.Application.Interfaces.IApplicationService</c> (transitively) must
/// live under the <c>Humans.Application.Services.*</c> namespace.
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Application</c> only. Per-section accuracy (e.g.
/// <c>CampService</c> in <c>Services.Camps</c> specifically, not
/// <c>Services.Foo</c>) is enforced for free by the C# compiler — callers using
/// <c>using Humans.Application.Services.Camps;</c> won't resolve to a service
/// that moved. This rule only enforces the prefix, replacing ~38 per-section
/// reflection assertions of the form
/// <c>ServiceName_LivesInHumansApplicationServices&lt;Section&gt;Namespace</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationServiceLocationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0012";

    private const string ExpectedNamespacePrefix = "Humans.Application.Services.";
    private const string IApplicationServiceFullName = "Humans.Application.Interfaces.IApplicationService";

    private static readonly LocalizableString Title =
        "Application service is outside Humans.Application.Services namespace";

    private static readonly LocalizableString MessageFormat =
        "'{0}' implements IApplicationService but lives in '{1}'. Move it under Humans.Application.Services.<Section>.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Application services (transitive implementers of IApplicationService) belong " +
            "under Humans.Application.Services.<Section> per design-rules §2b. Per-section " +
            "accuracy is enforced by the compiler — callers won't find a service that moved " +
            "out of its owning section's namespace.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Application, System.StringComparison.Ordinal))
            return;

        var marker = context.Compilation.GetTypeByMetadataName(IApplicationServiceFullName);
        if (marker is null)
            return;

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(ctx, marker),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol marker)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (!ImplementsMarker(type, marker))
            return;

        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ns.StartsWith(ExpectedNamespacePrefix, System.StringComparison.Ordinal))
            return;

        var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;
        context.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name, ns));
    }

    private static bool ImplementsMarker(INamedTypeSymbol type, INamedTypeSymbol marker)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, marker))
                return true;
        }
        return false;
    }
}
