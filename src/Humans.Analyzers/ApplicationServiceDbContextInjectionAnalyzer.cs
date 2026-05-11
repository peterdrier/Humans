using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationServiceDbContextInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0009";

    private static readonly LocalizableString Title =
        "Application services may not inject HumansDbContext";

    private static readonly LocalizableString MessageFormat =
        "Application service '{0}' must not inject HumansDbContext. Services should depend on repository interfaces for database access.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Application services own orchestration and business rules; database access goes through repository interfaces. " +
            "Injecting HumansDbContext directly erodes that boundary.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private const string ApplicationServicesNamespace = "Humans.Application.Services";
    private const string HumansDbContextFullName = "Humans.Infrastructure.Data.HumansDbContext";

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

        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class)
            return;

        if (!NamespaceStartsWith(type.ContainingNamespace, ApplicationServicesNamespace))
            return;

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (!string.Equals(parameter.Type.ToDisplayString(), HumansDbContextFullName, System.StringComparison.Ordinal))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0],
                    type.Name));
            }
        }
    }

    private static bool NamespaceStartsWith(INamespaceSymbol? ns, string prefix)
    {
        if (ns is null || ns.IsGlobalNamespace)
            return false;

        var name = ns.ToDisplayString();
        return string.Equals(name, prefix, System.StringComparison.Ordinal) ||
            name.StartsWith(prefix + ".", System.StringComparison.Ordinal);
    }
}
