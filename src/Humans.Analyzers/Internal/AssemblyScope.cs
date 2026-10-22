using System.Linq;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// What a symbol belongs to. Which assemblies get analyzed at all is decided by <c>src/Directory.Build.props</c>,
/// which attaches the analyzers to every <c>src/</c> project and nothing else; no rule re-derives that.
/// </summary>
internal static class AssemblyScope
{
    private const string ISectionFullName = "Humans.Base.Interfaces.ISection";
    private const string SectionEntryPointTypeName = "Section";

    /// <summary>
    /// True for a section assembly — one whose root namespace declares the
    /// <c>Section : ISection</c> entry point that boot discovery registers.
    /// </summary>
    /// <remarks>
    /// Looked up by metadata name so the check stays O(1) per compilation;
    /// <c>SectionEntryPointConventionTests</c> pins every section to that location.
    /// </remarks>
    public static bool IsSection(IAssemblySymbol assembly)
    {
        var entryPoint = assembly.GetTypeByMetadataName($"{assembly.Name}.{SectionEntryPointTypeName}");
        if (entryPoint is not { TypeKind: TypeKind.Class, IsAbstract: false })
            return false;

        return entryPoint.AllInterfaces.Any(i =>
            string.Equals(i.ToDisplayString(), ISectionFullName, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when <paramref name="symbol"/> is declared in a section's <c>Data/</c> folder —
    /// the section-local Infrastructure layer (<c>&lt;Section&gt;DbContext</c>, its
    /// configurations, its repositories, its interceptors).
    /// </summary>
    /// <remarks>
    /// A section assembly holds all three layers, so a rule scoped to "Application or Web" must be expressed by folder.
    /// Namespace segment first, file path second, like <c>PublicSurfaceRule.IsUnderContracts</c>, so it holds for
    /// analyzer test sources too.
    /// </remarks>
    public static bool IsInSectionDataLayer(ISymbol symbol)
    {
        if (!IsSection(symbol.ContainingAssembly))
            return false;

        var ns = symbol.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns.Split('.').Any(segment => string.Equals(segment, DataFolder, StringComparison.Ordinal)))
            return true;

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var filePath = syntaxRef.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath))
                continue;

            var normalized = "/" + filePath.Replace('\\', '/').TrimStart('/') + "/";
            if (normalized.IndexOf("/" + DataFolder + "/", StringComparison.Ordinal) >= 0)
                return true;
        }

        return false;
    }

    private const string DataFolder = "Data";
}
