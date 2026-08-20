using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Microsoft.Extensions.DependencyModel;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Boot discovery and <c>Humans.Analyzers</c>' <c>AssemblyScope.IsSection</c> must call the
/// same assemblies sections (nobodies-collective/Humans#1064).
/// </summary>
/// <remarks>
/// They ask different questions: discovery scans <c>GetExportedTypes()</c> for
/// <see cref="ISection"/>, the analyzer looks up <c>Humans.&lt;Section&gt;.Section</c> by
/// metadata name. Rename the entry point and only discovery still finds it; make it internal
/// and only the analyzer does. Either way the build stays green and one of the two quietly
/// stops covering that section.
/// <para>
/// Driven from every <c>Humans.*</c> assembly in the graph, not from the discovered set — a
/// sweep over what discovery returned cannot notice discovery returning less.
/// </para>
/// </remarks>
public sealed class SectionEntryPointConventionTests
{
    [HumansFact]
    public void DiscoveryAndTheAnalyzerAgreeOnWhichAssembliesAreSections()
    {
        var candidates = HumansAssemblies();

        // Base alone is half a dozen projects, so a graph this small means DependencyContext
        // came back empty and every assertion below would be vacuous.
        candidates.Should().HaveCountGreaterThan(20,
            because: "the dependency graph should hold Base plus every section and leaf");

        var discovered = SectionDiscoveryExtensions.SectionAssemblies().ToHashSet();

        var disagreements = candidates
            .Where(a => discovered.Contains(a) != DeclaresEntryPointWhereTheAnalyzerLooks(a))
            .Select(a => $"{a.GetName().Name}: discovery={discovered.Contains(a)}, "
                         + $"analyzer={DeclaresEntryPointWhereTheAnalyzerLooks(a)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        disagreements.Should().BeEmpty(
            because: "a section is one for both or neither — see the class remarks for how the "
                     + "two disagree, and note that deleting an entry point outright leaves both "
                     + "false and is caught by the section's render tests instead");
    }

    /// <summary>Mirrors <c>AssemblyScope.IsSection</c>: the type resolved by name, internals
    /// included, implementing <see cref="ISection"/>.</summary>
    private static bool DeclaresEntryPointWhereTheAnalyzerLooks(Assembly assembly) =>
        assembly.GetType($"{assembly.GetName().Name}.Section")
            is { IsClass: true, IsAbstract: false } entryPoint
        && typeof(ISection).IsAssignableFrom(entryPoint);

    /// <summary>Every loadable <c>Humans.*</c> assembly — sections, leaves and Base alike.</summary>
    private static IReadOnlyList<Assembly> HumansAssemblies() =>
        [.. DependencyContext.Default?.RuntimeLibraries
            .Where(l => l.Name.StartsWith("Humans.", StringComparison.Ordinal))
            .Select(l => TryLoad(l.Name))
            .OfType<Assembly>() ?? []];

    private static Assembly? TryLoad(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }
}
