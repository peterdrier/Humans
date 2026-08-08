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

    private const string SectionAttributeName = "SectionAttribute";

    public static bool IsApplicationOrWeb(IAssemblySymbol assembly) =>
        assembly.Name is Application or Web || IsSection(assembly);

    public static bool IsApplicationWebOrInfrastructure(IAssemblySymbol assembly) =>
        assembly.Name is Application or Web or Infrastructure || IsSection(assembly);

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
}
