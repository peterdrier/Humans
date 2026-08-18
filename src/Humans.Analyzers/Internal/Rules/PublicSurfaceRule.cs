using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers.Internal.Rules;

/// <summary>
/// HUM0034 — outside <c>Contracts/</c> a section's types are internal. Two kinds of
/// exception: a deliberate surface (<c>Contracts/</c>, <c>Jobs/</c>, the <c>ISection</c>
/// entry point, the <c>&lt;Section&gt;Resource</c> marker) and types the framework
/// silently drops when they are internal (view components, tag helpers, EF migrations).
/// </summary>
/// <remarks>
/// The membership test for the framework exception: does making the type internal fail
/// loudly, or render nothing? Razor's compile-time discovery filters on public and skips
/// what it cannot see — an internal view component ships <c>&lt;vc:…&gt;</c> as inert
/// markup with a green build. Runtime resolution throws, so it does not qualify.
/// Checked on declared accessibility: an unnecessary <c>public</c> on a type nested in an
/// internal one is a landmine, not a no-op.
/// </remarks>
internal static class PublicSurfaceRule
{
    private const string ISectionFullName = "Humans.Application.Interfaces.ISection";
    private const string EfMigrationFullName = "Microsoft.EntityFrameworkCore.Migrations.Migration";
    private const string IRepositoryFullName = "Humans.Application.Interfaces.Repositories.IRepository";
    private const string IRecurringJobFullName = "Humans.Application.Interfaces.IRecurringJob";
    private const string ViewComponentAttributeFullName = "Microsoft.AspNetCore.Mvc.ViewComponentAttribute";
    private const string NonViewComponentAttributeFullName = "Microsoft.AspNetCore.Mvc.NonViewComponentAttribute";
    private const string TagHelperInterfaceFullName = "Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper";
    private const string ResourceNameSuffix = "Resource";
    private const string ViewComponentNameSuffix = "ViewComponent";
    private const string JobNameSuffix = "Job";
    private const string ContractsSegment = "Contracts";
    private const string JobsSegment = "Jobs";

    public const string DiagnosticId = "HUM0034";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Public type outside a section's public surface",
        messageFormat:
            "'{0}' is public in section '{1}'. A section is internal by default: its public "
            + "surface is Contracts/, its Jobs/ (Hangfire jobs the Shell schedules by concrete "
            + "type), its Section entry point, its <Section>Resource marker, EF migrations, and "
            + "types the framework needs public (view components, tag helpers). Make '{0}' "
            + "internal, or move it under Contracts/ if another section needs it.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Every other public type is either an accident waiting to be depended on, or "
            + "belongs under Contracts/ or Jobs/ instead (design §10, §6a).");

    /// <summary>The well-known types the check needs, resolved once per compilation.</summary>
    public sealed class Context(
        INamedTypeSymbol? sectionMarker,
        INamedTypeSymbol? migrationBase,
        INamedTypeSymbol? repositoryMarker,
        INamedTypeSymbol? viewComponentAttr,
        INamedTypeSymbol? nonViewComponentAttr,
        INamedTypeSymbol? tagHelperInterface,
        INamedTypeSymbol? recurringJobInterface)
    {
        public INamedTypeSymbol? SectionMarker { get; } = sectionMarker;
        public INamedTypeSymbol? MigrationBase { get; } = migrationBase;
        public INamedTypeSymbol? RepositoryMarker { get; } = repositoryMarker;
        public INamedTypeSymbol? ViewComponentAttr { get; } = viewComponentAttr;
        public INamedTypeSymbol? NonViewComponentAttr { get; } = nonViewComponentAttr;
        public INamedTypeSymbol? TagHelperInterface { get; } = tagHelperInterface;
        public INamedTypeSymbol? RecurringJobInterface { get; } = recurringJobInterface;
    }

    public static Context Prepare(Compilation compilation) => new(
        compilation.GetTypeByMetadataName(ISectionFullName),
        compilation.GetTypeByMetadataName(EfMigrationFullName),
        compilation.GetTypeByMetadataName(IRepositoryFullName),
        compilation.GetTypeByMetadataName(ViewComponentAttributeFullName),
        compilation.GetTypeByMetadataName(NonViewComponentAttributeFullName),
        compilation.GetTypeByMetadataName(TagHelperInterfaceFullName),
        compilation.GetTypeByMetadataName(IRecurringJobFullName));

    public static void Analyze(
        SymbolAnalysisContext context,
        Context types,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.DeclaredAccessibility != Accessibility.Public || type.IsImplicitlyDeclared)
            return;

        if (IsSectionEntryPoint(type, types.SectionMarker)
            || IsResourceMarker(type)
            || IsEfMigration(type, types.MigrationBase)
            || IsViewComponent(type, types.ViewComponentAttr, types.NonViewComponentAttr)
            || IsTagHelper(type, types.TagHelperInterface)
            || IsUnderContracts(type)
            || IsAllowedJob(type, types.RecurringJobInterface))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: type.Locations.Length > 0 ? type.Locations[0] : Location.None,
            effectiveSeverity: GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId),
            additionalLocations: null,
            properties: null,
            messageArgs: [type.Name, type.ContainingAssembly.Name]));
    }

    /// <summary>Matched by implementing <c>ISection</c> — the test boot discovery uses.</summary>
    private static bool IsSectionEntryPoint(INamedTypeSymbol type, INamedTypeSymbol? sectionMarker) =>
        sectionMarker is not null
        && type is { TypeKind: TypeKind.Class, IsAbstract: false }
        && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, sectionMarker));

    /// <summary>Matched the way <c>SectionResourceTypes()</c> matches it at runtime.</summary>
    private static bool IsResourceMarker(INamedTypeSymbol type) =>
        type is { TypeKind: TypeKind.Class, IsAbstract: false }
        && type.Name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal);

    /// <summary>The scaffolder emits migrations public and they are never hand-edited.</summary>
    private static bool IsEfMigration(INamedTypeSymbol type, INamedTypeSymbol? migrationBase) =>
        migrationBase is not null && type.InheritsFromOrEquals(migrationBase.ToDisplayString());

    /// <summary>Matched exactly the way <c>ViewComponentConventions.IsComponent</c> does.</summary>
    public static bool IsViewComponent(
        INamedTypeSymbol type,
        INamedTypeSymbol? viewComponentAttr,
        INamedTypeSymbol? nonViewComponentAttr)
    {
        if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false })
            return false;
        if (HasAttribute(type, nonViewComponentAttr))
            return false;

        return type.Name.EndsWith(ViewComponentNameSuffix, StringComparison.Ordinal)
            || HasAttribute(type, viewComponentAttr);
    }

    /// <summary>Implementing <c>ITagHelper</c> is the whole of Razor's own test.</summary>
    private static bool IsTagHelper(INamedTypeSymbol type, INamedTypeSymbol? tagHelperInterface) =>
        tagHelperInterface is not null
        && type is { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false }
        && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, tagHelperInterface));

    private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol? attribute) =>
        attribute is not null
        && type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

    /// <summary>
    /// Declared under <c>Contracts/</c>. Namespace first, declaring file path second — the
    /// same dual check the other folder carve-outs use, so it holds for analyzer test
    /// sources too.
    /// </summary>
    public static bool IsUnderContracts(INamedTypeSymbol type) => IsUnderFolder(type, ContractsSegment);

    /// <summary>
    /// A Hangfire job declared under <c>Jobs/</c>: deliberate public surface — the Shell
    /// names the concrete type when scheduling or enqueueing it — but its audience is the
    /// Shell scheduler, not other sections, so it doesn't belong under Contracts/. Limited
    /// to <c>IRecurringJob</c> implementors (Shell's recurring roll-call) and the
    /// <c>*Job</c> naming convention (enqueue-style Hangfire jobs), so a stray non-job type
    /// dropped in Jobs/ still fires.
    /// </summary>
    private static bool IsAllowedJob(INamedTypeSymbol type, INamedTypeSymbol? recurringJobInterface)
    {
        if (type is not { TypeKind: TypeKind.Class, IsAbstract: false })
            return false;
        if (!IsUnderFolder(type, JobsSegment))
            return false;

        return (recurringJobInterface is not null
                && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, recurringJobInterface)))
            || type.Name.EndsWith(JobNameSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declared under <paramref name="segment"/>/. Namespace first, declaring file path
    /// second, so the check holds for analyzer test sources (namespace only) as well as
    /// production sources (namespace and file path agree).
    /// </summary>
    private static bool IsUnderFolder(INamedTypeSymbol type, string segment)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns.Split('.').Any(s => string.Equals(s, segment, StringComparison.Ordinal)))
            return true;

        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var filePath = syntaxRef.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath))
                continue;

            var normalized = "/" + filePath.Replace('\\', '/').TrimStart('/') + "/";
            if (normalized.IndexOf("/" + segment + "/", StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }
}
