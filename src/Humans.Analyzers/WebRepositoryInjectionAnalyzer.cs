using System.Linq;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0014 — an MVC surface (controller or view component) must not inject a repository.
/// It calls the section's application service; the service owns the repository
/// (peters-hard-rules: "Controllers … can not call repositories").
/// </summary>
/// <remarks>
/// <para>
/// The subject used to be "every class in <c>Humans.Web</c>", which worked while Shell was
/// the only Web layer. A section assembly holds all three layers at once
/// (nobodies-collective/Humans#866), so the subject is stated structurally instead —
/// matched the way MVC itself matches them, so the rule covers exactly what the framework
/// exposes and nothing that merely looks like it (nobodies-collective/Humans#1064).
/// </para>
/// <para>
/// The wider rule — "only an <c>IApplicationService</c> implementer may hold a repository"
/// — is the one this should eventually be, and it is blocked, not undesirable:
/// <c>Humans.Users.Contracts</c> dropped <c>IApplicationService</c> from seven interfaces
/// to keep its zero-reference property (G5 lane 3b, "the migration outranks the
/// analyzers"), so those seven services could not satisfy it today. Revisit when that
/// leaf can reference Base again.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WebRepositoryInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0014";

    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string ViewComponentNameSuffix = "ViewComponent";
    private const string ViewComponentAttributeFullName = "Microsoft.AspNetCore.Mvc.ViewComponentAttribute";
    private const string NonViewComponentAttributeFullName = "Microsoft.AspNetCore.Mvc.NonViewComponentAttribute";
    private const string IRepositoryFullName = "Humans.Application.Interfaces.Repositories.IRepository";

    private static readonly LocalizableString Title =
        "MVC surface injects a repository directly";

    private static readonly LocalizableString MessageFormat =
        "'{0}' injects '{1}'. A controller or view component calls the section's application "
        + "service; the service owns the repository.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A repository is the persistence boundary its own section's application service " +
            "owns. Controllers and view components call the service; reaching past it to the " +
            "repository collapses the layer (design-rules §2b, §3).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var repositoryMarker = context.Compilation.GetTypeByMetadataName(IRepositoryFullName);
        if (repositoryMarker is null)
            return;

        var viewComponentAttr = context.Compilation.GetTypeByMetadataName(ViewComponentAttributeFullName);
        var nonViewComponentAttr = context.Compilation.GetTypeByMetadataName(NonViewComponentAttributeFullName);
        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            c => AnalyzeNamedType(c, repositoryMarker, viewComponentAttr, nonViewComponentAttr, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol repositoryMarker,
        INamedTypeSymbol? viewComponentAttr,
        INamedTypeSymbol? nonViewComponentAttr,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (!IsMvcSurface(type, viewComponentAttr, nonViewComponentAttr))
            return;

        // The grandfather decision is made on the containing class, not the
        // parameter — the [Grandfathered] attribute can only target a type.
        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (!Implements(parameter.Type, repositoryMarker))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor: Rule,
                    location: parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0],
                    effectiveSeverity: severity,
                    additionalLocations: null,
                    properties: null,
                    messageArgs: [type.Name, parameter.Type.Name]));
            }
        }
    }

    /// <summary>
    /// A controller or a view component, matched the way MVC matches them —
    /// <c>ControllerBase</c> inheritance, and
    /// <c>ViewComponentConventions.IsComponent</c>'s name-or-attribute test minus
    /// <c>[NonViewComponent]</c>.
    /// </summary>
    private static bool IsMvcSurface(
        INamedTypeSymbol type,
        INamedTypeSymbol? viewComponentAttr,
        INamedTypeSymbol? nonViewComponentAttr)
    {
        if (type.InheritsFromOrEquals(ControllerBaseFullName))
            return true;

        if (type.IsGenericType || HasAttribute(type, nonViewComponentAttr))
            return false;

        return type.Name.EndsWith(ViewComponentNameSuffix, System.StringComparison.Ordinal)
            || HasAttribute(type, viewComponentAttr);
    }

    private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol? attribute) =>
        attribute is not null
        && type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol marker)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        if (SymbolEqualityComparer.Default.Equals(named, marker))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, marker))
                return true;
        }
        return false;
    }
}
