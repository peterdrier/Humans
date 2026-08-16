using System.Linq;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// What a symbol belongs to, for the rules that need to know. Which assemblies get
/// analyzed at all is decided by <c>src/Directory.Build.props</c>, which attaches the
/// analyzers to every <c>src/</c> project and to nothing else — not the analyzers
/// themselves, not test projects, not generated compilations. No rule re-derives that
/// at analysis time.
/// </summary>
internal static class AssemblyScope
{
    private const string ISectionFullName = "Humans.Application.Interfaces.ISection";
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
            string.Equals(i.ToDisplayString(), ISectionFullName, System.StringComparison.Ordinal));
    }

    /// <summary>
    /// True when <paramref name="symbol"/> is declared in a section's <c>Data/</c> folder —
    /// the section-local Infrastructure layer (<c>&lt;Section&gt;DbContext</c>, its
    /// configurations, its repositories, its interceptors).
    /// </summary>
    /// <remarks>
    /// A section assembly holds what used to be three assemblies, so a rule scoped to
    /// "Application or Web" cannot be expressed by assembly name inside one. Before G5,
    /// <c>UserRepository</c> writing <c>User.Email</c> was out of HUM0002's scope because the
    /// file lived in <c>Humans.Infrastructure</c>; after the move the identical code is in a
    /// section assembly, and the rule would fail the build on the one writer it exists to
    /// protect (design §15 step 6a's "a move can put code into a sweep as easily as out of
    /// one", reached from the other direction). Matched on the namespace segment first and the
    /// file path second, the same dual check <c>PublicSurfaceRule.IsUnderContracts</c>
    /// uses, so the carve-out holds for real code and for analyzer test sources alike.
    /// </remarks>
    public static bool IsInSectionDataLayer(ISymbol symbol)
    {
        if (!IsSection(symbol.ContainingAssembly))
            return false;

        var ns = symbol.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns.Split('.').Any(segment => string.Equals(segment, DataFolder, System.StringComparison.Ordinal)))
            return true;

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var filePath = syntaxRef.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath))
                continue;

            var normalized = "/" + filePath.Replace('\\', '/').TrimStart('/') + "/";
            if (normalized.IndexOf("/" + DataFolder + "/", System.StringComparison.Ordinal) >= 0)
                return true;
        }

        return false;
    }

    private const string DataFolder = "Data";
}
