using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Microsoft.Extensions.DependencyModel;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Boot discovery (<c>GetExportedTypes()</c>) and the analyzers' <c>AssemblyScope.IsSection</c> (lookup by name) must
/// agree. Rename the entry point and only discovery finds it; make it internal and only the analyzer does.
/// </summary>
/// <remarks>Driven from every <c>Humans.*</c> assembly, since a sweep over discovery's output can't see it shrink.</remarks>
public sealed class SectionEntryPointConventionTests
{
    [HumansFact]
    public void DiscoveryAndTheAnalyzerAgreeOnWhichAssembliesAreSections()
    {
        var candidates = HumansAssemblies();

        // A graph this small means DependencyContext came back empty and every assertion would be vacuous.
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

    /// <summary>Mirrors <c>AssemblyScope.IsSection</c>, internals included.</summary>
    private static bool DeclaresEntryPointWhereTheAnalyzerLooks(Assembly assembly) =>
        assembly.GetType($"{assembly.GetName().Name}.Section")
            is { IsClass: true, IsAbstract: false } entryPoint
        && typeof(ISection).IsAssignableFrom(entryPoint);

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
