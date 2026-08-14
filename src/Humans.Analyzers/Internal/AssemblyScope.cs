using System.Linq;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Internal;

/// <summary>
/// Which assemblies an analyzer polices. Most of the rule set applies to the
/// application's own code and to nothing else — not to the analyzers, not to test
/// projects, not to generated compilations.
/// </summary>
/// <remarks>
/// Section projects (nobodies-collective/Humans#866, G5) carry
/// <c>[assembly: Section("…")]</c> and are recognised by that marker rather than by
/// name. Keying on the three literal names alone would silently switch 22 of the 27
/// rules off inside the section that just moved, which would make the split *reduce*
/// enforcement — the exact inversion of its purpose. The marker also says what the
/// project is, where a "starts with Humans." prefix rule would only guess.
/// </remarks>
internal static class AssemblyScope
{
    public const string Application = "Humans.Application";
    public const string Web = "Humans.Web";
    public const string Infrastructure = "Humans.Infrastructure";
    public const string Domain = "Humans.Domain";

    private const string SectionAttributeName = "SectionAttribute";

    public static bool IsApplicationOrWeb(IAssemblySymbol assembly) =>
        assembly.Name is Application or Web || IsSection(assembly);

    public static bool IsApplicationWebOrInfrastructure(IAssemblySymbol assembly) =>
        assembly.Name is Application or Web or Infrastructure || IsSection(assembly);

    /// <summary>
    /// True for any of the application's own production assemblies, sections included.
    /// </summary>
    /// <remarks>
    /// Replaces the hardcoded four-name sets that <c>ConcurrencyTokenAnalyzer</c> and
    /// <c>DateTimeFormatStringAnalyzer</c> each carried. Neither named a section, so both went
    /// quiet inside every section that has moved so far — the §10 silent-drop shape, in the one
    /// place §15 step 11 says to grep for before each move. Found at Expenses' move
    /// (nobodies-collective/Humans#866, A3); the coverage it restores is retroactive for
    /// SystemSettings, Events, Store, Containers and Finance too.
    /// </remarks>
    public static bool IsProduction(IAssemblySymbol assembly) =>
        assembly.Name is Application or Web or Infrastructure or Domain || IsSection(assembly);

    /// <summary>
    /// True when the compilation is <paramref name="layer"/>, or any section assembly.
    /// </summary>
    /// <remarks>
    /// A section holds what used to be this vertical's Application, Web and Infrastructure
    /// code in one assembly, so it belongs to all three layer scopes at once. Analyzers
    /// that gated on a bare <c>assembly.Name == layer</c> comparison went silent inside a
    /// section the moment it moved; this is the replacement for that comparison wherever
    /// the rule is still live within a section.
    /// </remarks>
    public static bool IsLayerOrSection(IAssemblySymbol assembly, string layer) =>
        string.Equals(assembly.Name, layer, System.StringComparison.Ordinal) || IsSection(assembly);

    /// <summary>
    /// True for a section assembly — one carrying <c>[assembly: Section("…")]</c>.
    /// A single metadata read, where scanning for <c>ISection</c> implementations
    /// would cost a full declared-type walk per compilation.
    /// </summary>
    public static bool IsSection(IAssemblySymbol assembly) =>
        assembly.GetAttributes().Any(a =>
            string.Equals(a.AttributeClass?.Name, SectionAttributeName, System.StringComparison.Ordinal));

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
    /// file path second, the same dual check <c>SectionPublicSurfaceAnalyzer.IsUnderContracts</c>
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
