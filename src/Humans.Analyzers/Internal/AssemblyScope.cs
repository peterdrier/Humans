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
    /// True for a section assembly — one carrying <c>[assembly: Section("…")]</c>.
    /// A single metadata read, where scanning for <c>ISection</c> implementations
    /// would cost a full declared-type walk per compilation.
    /// </summary>
    public static bool IsSection(IAssemblySymbol assembly) =>
        assembly.GetAttributes().Any(a =>
            string.Equals(a.AttributeClass?.Name, SectionAttributeName, System.StringComparison.Ordinal));
}
