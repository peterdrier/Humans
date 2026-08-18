using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Shared section resolution for section-aware analyzers (HUM0032). A section
/// project's types take their section from the assembly name; what is left in
/// <c>Humans.Application</c> takes it from the namespace segment after a
/// well-known prefix.
/// </summary>
internal static class Sections
{
    public const string ServiceNamespacePrefix = "Humans.Application.Services.";
    public const string InterfaceNamespacePrefix = "Humans.Application.Interfaces.";

    private const string AssemblyPrefix = "Humans.";
    private const string ContractsSuffix = ".Contracts";

    /// <summary>
    /// Returns the section segment of <c>{prefix}{Section}[.*]</c> for
    /// <paramref name="type"/>, or null when the type is not declared under
    /// <paramref name="namespacePrefix"/>.
    /// </summary>
    public static string? FromNamespace(INamedTypeSymbol type, string namespacePrefix)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is null || !ns.StartsWith(namespacePrefix, StringComparison.Ordinal))
            return null;

        var startIndex = namespacePrefix.Length;
        if (startIndex >= ns.Length)
            return null;

        var dot = ns.IndexOf('.', startIndex);
        return dot < 0 ? ns.Substring(startIndex) : ns.Substring(startIndex, dot - startIndex);
    }

    /// <summary>
    /// The section a type belongs to: its assembly's, or — for what is left in
    /// <c>Humans.Application</c> — its namespace segment under
    /// <paramref name="namespacePrefix"/>.
    /// </summary>
    public static string? Of(INamedTypeSymbol type, string namespacePrefix) =>
        FromAssembly(type.ContainingAssembly) ?? FromNamespace(type, namespacePrefix);

    /// <summary>
    /// A section assembly's section: its name without the <c>Humans.</c> prefix, and
    /// without the <c>.Contracts</c> suffix — <c>Humans.Camps</c> and
    /// <c>Humans.Camps.Contracts</c> are both section Camps. Null for everything else,
    /// so a type left in a Base assembly falls through to namespace resolution.
    /// </summary>
    /// <remarks>
    /// A <c>.Contracts</c> assembly declares no <c>ISection</c>, so it is matched by name;
    /// everything else must pass <see cref="AssemblyScope.IsSection"/>, which keeps
    /// <c>Humans.Application</c> and <c>Humans.Interfaces</c> from reading as sections.
    /// </remarks>
    public static string? FromAssembly(IAssemblySymbol? assembly)
    {
        var name = assembly?.Name;
        if (name is null || !name.StartsWith(AssemblyPrefix, StringComparison.Ordinal))
            return null;

        if (name.EndsWith(ContractsSuffix, StringComparison.Ordinal))
        {
            var length = name.Length - AssemblyPrefix.Length - ContractsSuffix.Length;
            return length > 0 ? name.Substring(AssemblyPrefix.Length, length) : null;
        }

        return AssemblyScope.IsSection(assembly!) ? name.Substring(AssemblyPrefix.Length) : null;
    }
}
