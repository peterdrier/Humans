using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0012 — Every concrete class that implements
/// <c>Humans.Application.Interfaces.IApplicationService</c> (transitively) must
/// live in the namespace that matches the section of its primary service
/// interface.
/// </summary>
/// <remarks>
/// <para>
/// Convention: a service interface
/// <c>Humans.Application.Interfaces.&lt;Section&gt;.IXxxService</c> (one that
/// extends <c>IApplicationService</c>) must be implemented by a class in
/// <c>Humans.Application.Services.&lt;Section&gt;</c> (or a sub-namespace of it).
/// If the service interface lives directly in
/// <c>Humans.Application.Interfaces</c> (no section), any namespace under
/// <c>Humans.Application.Services</c> is accepted.
/// </para>
/// <para>
/// Runs in <c>Humans.Application</c> only. Replaces the ~38 per-section
/// reflection assertions of the form
/// <c>ServiceName_LivesInHumansApplicationServices&lt;Section&gt;Namespace</c>
/// AND enforces per-section ownership (which the looser prefix check did not).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplicationServiceLocationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0012";

    private const string ApplicationInterfacesPrefix = "Humans.Application.Interfaces";
    private const string ApplicationServicesNamespace = "Humans.Application.Services";
    private const string IApplicationServiceFullName = "Humans.Application.Interfaces.IApplicationService";

    private static readonly LocalizableString Title =
        "Application service is in the wrong section namespace";

    private static readonly LocalizableString MessageFormat =
        "'{0}' lives in '{1}' but its service interface(s) require it under one of: {2}";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "An application service must live in Humans.Application.Services.<Section> matching " +
            "the section of its primary IApplicationService-extending interface. Moving a service " +
            "across section namespaces erodes section ownership silently — the compiler accepts " +
            "the move once callers' usings are updated, but the architectural contract is broken " +
            "(design-rules §2b, §6).");

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

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(ctx, marker, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol marker,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (!ImplementsMarker(type, marker))
            return;

        var classNamespace = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;

        // Collect every accepted namespace for this class based on its service
        // interfaces. An accepted namespace is exactly one match per interface;
        // the class only needs to satisfy one of them to be considered correct.
        var expectedNamespaces = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, marker))
                continue;
            if (!ImplementsMarker(iface, marker))
                continue;

            var ifaceNamespace = iface.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (TryDeriveExpectedNamespace(ifaceNamespace, out var expected))
                expectedNamespaces.Add(expected);
        }

        if (expectedNamespaces.Count == 0)
        {
            // No section-typed interface — fall back to the loose prefix check.
            if (classNamespace.StartsWith(ApplicationServicesNamespace + ".", System.StringComparison.Ordinal)
                || string.Equals(classNamespace, ApplicationServicesNamespace, System.StringComparison.Ordinal))
                return;

            ReportDiagnostic(context, type, grandfatheredAttr, classNamespace, ApplicationServicesNamespace + ".*");
            return;
        }

        foreach (var expected in expectedNamespaces)
        {
            if (string.Equals(classNamespace, expected, System.StringComparison.Ordinal))
                return;
            if (classNamespace.StartsWith(expected + ".", System.StringComparison.Ordinal))
                return;
        }

        var formatted = string.Join(", ", expectedNamespaces);
        ReportDiagnostic(context, type, grandfatheredAttr, classNamespace, formatted);
    }

    private static bool TryDeriveExpectedNamespace(string interfaceNamespace, out string expected)
    {
        // "Humans.Application.Interfaces" exactly → expected "Humans.Application.Services".
        if (string.Equals(interfaceNamespace, ApplicationInterfacesPrefix, System.StringComparison.Ordinal))
        {
            expected = ApplicationServicesNamespace;
            return true;
        }

        // "Humans.Application.Interfaces.<rest>" → expected "Humans.Application.Services.<rest>".
        var prefixWithDot = ApplicationInterfacesPrefix + ".";
        if (interfaceNamespace.StartsWith(prefixWithDot, System.StringComparison.Ordinal))
        {
            var rest = interfaceNamespace.Substring(prefixWithDot.Length);
            expected = ApplicationServicesNamespace + "." + rest;
            return true;
        }

        expected = string.Empty;
        return false;
    }

    private static void ReportDiagnostic(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        INamedTypeSymbol? grandfatheredAttr,
        string actualNamespace,
        string expectedFormatted)
    {
        var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;
        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: location,
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: new object[] { type.Name, actualNamespace, expectedFormatted }));
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
