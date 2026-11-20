using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Deactivating a section that Shell or a running section consumes is reported, never thrown. No section is named here
/// except in <see cref="ShellNoLongerPinsTheNonCoreSections"/>: anywhere else that would be the pinned list the
/// mechanism exists to avoid.
/// </summary>
public sealed class SectionActivationTests
{
    private static IReadOnlyList<Assembly> Discovered => SectionDiscoveryExtensions.SectionAssemblies();

    /// <summary>Always runs, and consumes sections of its own.</summary>
    private static Assembly Shell => typeof(SectionActivation).Assembly;

    private static IReadOnlySet<string> Active(params string[] sections) =>
        sections.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> AllActive() =>
        Discovered.Select(SectionName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [HumansFact]
    public void EverySectionShipsActive()
    {
        var inactive = Discovered
            .SelectMany(a => a.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISection).IsAssignableFrom(t))
                .Select(t => (Section: SectionName(a), Instance: (ISection)Activator.CreateInstance(t)!)))
            .Where(s => !s.Instance.IsActive)
            .Select(s => s.Section)
            .ToList();

        inactive.Should().BeEmpty(
            because: "IsActive defaults to true, and no section overrides it today — a deployment "
                     + "that wants one off is what overriding is for");
    }

    [HumansFact]
    public void EverySectionActiveMeetsEveryDependency()
    {
        SectionActivation.UnmetDependencies(Discovered, Shell, AllActive()).Should().BeEmpty(
            because: "the shipped default runs every section that ships");
    }

    [HumansFact]
    public void DeactivatingASectionShellItselfConsumesIsReportedNotThrown()
    {
        var shellDependencies = SectionActivation.ShellDependencies(Discovered, Shell);
        shellDependencies.Should().NotBeEmpty(
            because: "Shell's own controllers name section Contracts types");

        var dropped = shellDependencies[0];
        var active = Active([.. Discovered.Select(SectionName)
            .Where(name => !name.Equals(dropped, StringComparison.OrdinalIgnoreCase))]);

        var unmet = SectionActivation.UnmetDependencies(Discovered, Shell, active);

        unmet.Should().ContainKey(dropped);
        unmet[dropped].Should().Contain(SectionName(Shell),
            because: "Shell always runs, so whatever it consumes is reported when deactivated");
    }

    [HumansFact]
    public void DeactivatingAConsumedSectionNamesTheDependents()
    {
        var (dependent, dependency) = FirstEdge();

        var unmet = SectionActivation.UnmetDependencies(Discovered, Shell, Active(dependent));

        unmet.Should().ContainKey(dependency);
        unmet[dependency].Should().Contain(dependent,
            because: "the report has to say what is missing and who wanted it");
    }

    /// <summary>
    /// The one place sections are named: these were Shell's compiled pins before their bindings, policies and views
    /// moved behind seams, and a new Shell reference to any of them re-pins it.
    /// </summary>
    [HumansFact]
    public void ShellNoLongerPinsTheNonCoreSections()
    {
        SectionActivation.ShellDependencies(Discovered, Shell).Should().NotContain(
            ["Backdoor", "Consent", "Governance", "Google", "Shifts", "Tickets"]);
    }

    [HumansFact]
    public void DependencyGraphResolvesAReferencedContractsAssemblyToItsSection()
    {
        var graph = SectionActivation.DependencyGraph(Discovered);
        var names = Discovered.Select(SectionName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var contractsEdges = Discovered
            .SelectMany(a => a.GetReferencedAssemblies()
                .Select(r => r.Name)
                .OfType<string>()
                .Where(n => n.StartsWith("Humans.", StringComparison.Ordinal)
                            && n.EndsWith(".Contracts", StringComparison.Ordinal))
                .Select(n => (
                    Dependent: SectionName(a),
                    Dependency: n["Humans.".Length..^".Contracts".Length])))
            .Where(e => names.Contains(e.Dependency)
                        && !e.Dependency.Equals(e.Dependent, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        contractsEdges.Should().NotBeEmpty(
            because: "sections reach each other through Contracts assemblies, so the closure "
                     + "check is vacuous if none of those references resolve back to a section");

        contractsEdges.Should().OnlyContain(e => graph[e.Dependent].Contains(e.Dependency));
    }

    [HumansFact]
    public void DependencyGraphCoversEverySectionAndNothingElse()
    {
        var graph = SectionActivation.DependencyGraph(Discovered);
        var names = Discovered.Select(SectionName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        graph.Keys.Should().BeEquivalentTo(names);
        graph.Should().OnlyContain(e => e.Value.All(d => names.Contains(d) && d != e.Key));
    }

    private static (string Dependent, string Dependency) FirstEdge()
    {
        var edge = SectionActivation.DependencyGraph(Discovered)
            .Where(e => e.Value.Count > 0)
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => (Dependent: e.Key, Dependency: e.Value[0]))
            .FirstOrDefault();

        edge.Dependent.Should().NotBeNull(because: "sections do consume each other's Contracts");
        return edge;
    }

    private static string SectionName(Assembly assembly) =>
        assembly.GetName().Name!["Humans.".Length..];
}
