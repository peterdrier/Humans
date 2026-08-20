using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// The activation mechanism (nobodies-collective/Humans#1081): a section declares whether
/// this deployment runs it via <see cref="ISection.IsActive"/>, and deactivating one that
/// Shell or another running section consumes fails startup.
/// </summary>
/// <remarks>
/// Every case drives off what discovery actually found and what the assemblies actually
/// reference — no section is named here, because naming one would be the pinned list the
/// mechanism exists to avoid.
/// </remarks>
public sealed class SectionActivationTests
{
    private static IReadOnlyList<Assembly> Discovered => SectionDiscoveryExtensions.SectionAssemblies();

    /// <summary>The Shell assembly, which always runs and consumes sections of its own.</summary>
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
        var act = () => SectionActivation.ThrowOnUnmetDependencies(Discovered, Shell, AllActive());

        act.Should().NotThrow(because: "the shipped default runs every section that ships");
    }

    [HumansFact]
    public void DeactivatingASectionShellItselfConsumesFails()
    {
        var shellDependencies = SectionActivation.ShellDependencies(Discovered, Shell);
        shellDependencies.Should().NotBeEmpty(
            because: "Shell's own controllers name section Contracts types");

        var dropped = shellDependencies[0];
        var active = Active([.. Discovered.Select(SectionName)
            .Where(name => !name.Equals(dropped, StringComparison.OrdinalIgnoreCase))]);

        var act = () => SectionActivation.ThrowOnUnmetDependencies(Discovered, Shell, active);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{dropped}*{SectionName(Shell)}*",
                because: "Shell always runs, so nothing may deactivate what Shell consumes");
    }

    [HumansFact]
    public void DeactivatingAConsumedSectionFailsAndNamesTheDependents()
    {
        var (dependent, dependency) = FirstEdge();

        var act = () => SectionActivation.ThrowOnUnmetDependencies(Discovered, Shell, Active(dependent));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{dependency}*{dependent}*",
                because: "the message has to say what is missing and who wanted it");
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

    /// <summary>Any real section-to-section edge, chosen deterministically.</summary>
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
