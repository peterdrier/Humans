using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Shared namespace→section resolution for section-aware analyzers
/// (HUM0017, HUM0032). A type's section is the first namespace segment after
/// a well-known prefix, folded per
/// <c>memory/architecture/users-profiles-one-section.md</c>.
/// </summary>
internal static class Sections
{
    public const string ServiceNamespacePrefix = "Humans.Application.Services.";
    public const string InterfaceNamespacePrefix = "Humans.Application.Interfaces.";

    /// <summary>
    /// Returns the folded section segment of
    /// <c>{prefix}{Section}[.*]</c> for <paramref name="type"/>, or null when
    /// the type is not declared under <paramref name="namespacePrefix"/>.
    /// </summary>
    public static string? FromNamespace(INamedTypeSymbol type, string namespacePrefix)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is null || !ns.StartsWith(namespacePrefix, System.StringComparison.Ordinal))
            return null;

        var startIndex = namespacePrefix.Length;
        if (startIndex >= ns.Length)
            return null;

        var dot = ns.IndexOf('.', startIndex);
        var raw = dot < 0 ? ns.Substring(startIndex) : ns.Substring(startIndex, dot - startIndex);
        return Fold(raw);
    }

    /// <summary>
    /// The section a type belongs to: its namespace segment under
    /// <paramref name="namespacePrefix"/>, or — for a type in a section project
    /// (nobodies-collective/Humans#866) — the name on its assembly's
    /// <c>[Section("…")]</c>.
    /// </summary>
    /// <remarks>
    /// A section's own types live under <c>Humans.&lt;Section&gt;.*</c>, not under the
    /// Base prefixes, so namespace resolution alone returns null for every one of them —
    /// and a section-aware analyzer that exits on null stops inspecting the section
    /// entirely.
    /// </remarks>
    public static string? Of(INamedTypeSymbol type, string namespacePrefix, INamedTypeSymbol? sectionAttr) =>
        FromNamespace(type, namespacePrefix) ?? FromAssembly(type.ContainingAssembly, sectionAttr);

    /// <summary>The section name on an assembly's <c>[Section("…")]</c>, if it has one.</summary>
    public static string? FromAssembly(IAssemblySymbol? assembly, INamedTypeSymbol? sectionAttr)
    {
        if (sectionAttr is null)
            return null;

        foreach (var attr in assembly?.GetAttributes() ?? default)
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, sectionAttr))
                continue;
            if (attr.ConstructorArguments.Length == 0)
                continue;
            return Fold(attr.ConstructorArguments[0].Value as string);
        }
        return null;
    }

    /// <summary>
    /// Mirrors <c>ServiceBoundaryArchitectureTests.ServiceSection</c>: the
    /// Users + Profiles ownership merger means <c>Users</c>, <c>Profile</c>,
    /// and <c>Profiles</c> all resolve to the unified <c>"Humans"</c> section,
    /// which section-aware analyzers treat as a single intra-section domain.
    /// Per <c>memory/architecture/users-profiles-one-section.md</c>.
    /// </summary>
    public static string? Fold(string? raw)
    {
        if (raw is null)
            return null;
        return raw switch
        {
            "Users" or "Profile" or "Profiles" => "Humans",
            _ => raw,
        };
    }
}
