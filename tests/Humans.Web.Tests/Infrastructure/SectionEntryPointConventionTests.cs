using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Keeps boot discovery and <c>Humans.Analyzers</c>' <c>AssemblyScope.IsSection</c> looking
/// at the same 42 assemblies (nobodies-collective/Humans#1064).
/// </summary>
/// <remarks>
/// Discovery walks every exported type looking for <see cref="ISection"/>; the analyzer
/// cannot afford that per compilation and does an O(1) metadata lookup of
/// <c>Humans.&lt;Section&gt;.Section</c> instead. The two agree only while every section
/// puts its entry point in its assembly's root namespace under that name. Move one and the
/// analyzer stops seeing that project as a section — 22 rules go quiet inside it with a
/// green build, the exact silent-drop this test exists to make loud.
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
