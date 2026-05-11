using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ControllerDbContextInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0008";

    private static readonly LocalizableString Title =
        "Controllers may not inject HumansDbContext";

    private static readonly LocalizableString MessageFormat =
        "Controller '{0}' must not inject HumansDbContext. Controllers should call services; services go through repositories or infrastructure-owned database services.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Controllers reaching directly for HumansDbContext bypass the service and repository layers. " +
            "Keep database access behind an application or infrastructure service and inject that service instead.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string HumansDbContextFullName = "Humans.Infrastructure.Data.HumansDbContext";

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

        var dbContextType = context.Compilation.GetTypeByMetadataName(HumansDbContextFullName);
        if (dbContextType is null)
            return;

        context.RegisterSymbolAction(c => AnalyzeNamedType(c, dbContextType), SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol dbContextType)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.InheritsFromOrEquals(ControllerBaseFullName))
            return;

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (!SymbolEqualityComparer.Default.Equals(parameter.Type, dbContextType))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0],
                    type.Name));
            }
        }
    }
}
