using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0017 — A concrete <see cref="Humans.Application.Interfaces.IApplicationService"/>
/// implementer in <c>Humans.Application.Services.{X}</c> must not inject a
/// repository (<see cref="Humans.Application.Interfaces.Repositories.IRepository"/>
/// extender) marked <c>[Section("Y")]</c> when X != Y. Cross-section
/// reads/writes go through the owning section's application service, never
/// its repository (design-rules §6).
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Application</c> only. The repository's section is
/// declared with <see cref="Humans.Domain.Attributes.SectionAttribute"/>
/// because repo interfaces sit in the flat
/// <c>Humans.Application.Interfaces.Repositories</c> namespace (HUM0013) and
/// the implementations in <c>Humans.Infrastructure</c> are not visible to
/// this compilation.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CrossSectionRepositoryInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0017";
    public const string IndeterminateSectionId = "HUM0018";

    private const string ServiceNamespacePrefix = "Humans.Application.Services.";
    private const string ApplicationServiceMarkerFullName = "Humans.Application.Interfaces.IApplicationService";
    private const string RepositoryMarkerFullName = "Humans.Application.Interfaces.Repositories.IRepository";
    private const string SectionAttributeFullName = "Humans.Domain.Attributes.SectionAttribute";

    private static readonly LocalizableString CrossSectionTitle =
        "Application service injects a cross-section repository";

    private static readonly LocalizableString CrossSectionMessageFormat =
        "'{0}' (section '{1}') injects '{2}' which belongs to section '{3}'. Reach across sections through the owning section's application service, not its repository.";

    private static readonly LocalizableString IndeterminateTitle =
        "Section cannot be determined";

    private static readonly LocalizableString IndeterminateMessageFormat =
        "{0}";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: CrossSectionTitle,
        messageFormat: CrossSectionMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "An application service owns its section's repository surface. Calling " +
            "another section's repository directly bypasses that section's service " +
            "(caching, authorization, invariants) and couples write paths to private " +
            "schema. Inject the foreign section's IXxxService instead, or move the " +
            "data flow to a domain event / orchestration service. See design-rules §6.");

    public static readonly DiagnosticDescriptor IndeterminateRule = new(
        id: IndeterminateSectionId,
        title: IndeterminateTitle,
        messageFormat: IndeterminateMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Shared diagnostic raised whenever an analyzer needs the section of a type " +
            "and cannot determine it — either the type is not declared under " +
            "Humans.Application.Services.<Section>, or its interface (for repositories " +
            "and other cross-section-marked surfaces) is missing [Section(\"<Name>\")]. " +
            "HUM0017 is the first consumer; future section-aware rules will share this " +
            "id. Fix by marking the type so all section-aware analyzers can apply.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule, IndeterminateRule];

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

        var applicationServiceMarker = context.Compilation.GetTypeByMetadataName(ApplicationServiceMarkerFullName);
        var repositoryMarker = context.Compilation.GetTypeByMetadataName(RepositoryMarkerFullName);
        var sectionAttr = context.Compilation.GetTypeByMetadataName(SectionAttributeFullName);
        if (applicationServiceMarker is null || repositoryMarker is null || sectionAttr is null)
            return;

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(ctx, applicationServiceMarker, repositoryMarker, sectionAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol applicationServiceMarker,
        INamedTypeSymbol repositoryMarker,
        INamedTypeSymbol sectionAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (!ImplementsMarker(type, applicationServiceMarker))
            return;

        // If the service's own section is undetermined, HUM0012 (services must be
        // declared under Humans.Application.Services.<Section>) is already the
        // right diagnostic — don't pile on with a HUM0018 here. Just exit; HUM0017
        // cannot be applied to this class until HUM0012 is satisfied.
        var serviceSection = ExtractServiceSection(type);
        if (serviceSection is null)
            return;

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (!ImplementsMarker(parameter.Type, repositoryMarker))
                    continue;

                var paramLocation = parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0];
                var dependencySection = ReadSection(parameter.Type, sectionAttr);
                if (dependencySection is null)
                {
                    var paramMessage =
                        $"'{type.Name}' injects repository '{parameter.Type.Name}' which is not " +
                        "marked [Section]. Add [Section(\"<Name>\")] to the repository interface " +
                        "(matching its implementation's namespace) so HUM0017 can verify the " +
                        "injection does not cross section boundaries.";
                    context.ReportDiagnostic(Diagnostic.Create(
                        descriptor: IndeterminateRule,
                        location: paramLocation,
                        messageArgs: [paramMessage]));
                    continue;
                }

                if (string.Equals(serviceSection, dependencySection, System.StringComparison.Ordinal))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor: Rule,
                    location: paramLocation,
                    messageArgs: [type.Name, serviceSection, parameter.Type.Name, dependencySection]));
            }
        }
    }

    private static bool ImplementsMarker(ITypeSymbol type, INamedTypeSymbol marker)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        foreach (var iface in named.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, marker))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the section segment of <c>Humans.Application.Services.{Section}[.*]</c>
    /// or null if the type is not in a sectioned service namespace.
    /// </summary>
    private static string? ExtractServiceSection(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is null || !ns.StartsWith(ServiceNamespacePrefix, System.StringComparison.Ordinal))
            return null;

        var startIndex = ServiceNamespacePrefix.Length;
        if (startIndex >= ns.Length)
            return null;

        var dot = ns.IndexOf('.', startIndex);
        return dot < 0 ? ns.Substring(startIndex) : ns.Substring(startIndex, dot - startIndex);
    }

    private static string? ReadSection(ITypeSymbol type, INamedTypeSymbol sectionAttr)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, sectionAttr))
                continue;
            if (attr.ConstructorArguments.Length == 0)
                continue;
            return attr.ConstructorArguments[0].Value as string;
        }
        return null;
    }
}
