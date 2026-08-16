using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Keeps boot discovery and <c>Humans.Analyzers</c>' <c>AssemblyScope.IsSection</c> looking
/// at the same 42 assemblies (nobodies-collective/Humans#1064).
/// </summary>
/// <remarks>
/// Discovery scans for <see cref="ISection"/>; the analyzer looks up
/// <c>Humans.&lt;Section&gt;.Section</c> by name. Move an entry point and the analyzer stops
/// seeing that project as a section, with a green build — this makes that loud.
/// </remarks>
public sealed class SectionEntryPointConventionTests
{
    [HumansFact]
    public void EverySectionDeclaresItsEntryPointWhereTheAnalyzerLooks()
    {
        var sectionAssemblies = SectionDiscoveryExtensions.SectionAssemblies();
        sectionAssemblies.Should().NotBeEmpty(
            because: "a sweep that finds no section assemblies passes vacuously");

        foreach (var assembly in sectionAssemblies)
        {
            var expectedName = $"{assembly.GetName().Name}.Section";
            var entryPoint = assembly.GetType(expectedName);

            entryPoint.Should().NotBeNull(
                because: $"AssemblyScope.IsSection resolves '{expectedName}' by name");
            typeof(ISection).IsAssignableFrom(entryPoint).Should().BeTrue(
                because: $"'{expectedName}' is what the analyzer tests for ISection");
        }
    }
}
