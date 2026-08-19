using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Extensions;
using Microsoft.Extensions.Configuration;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// The activation mechanism (nobodies-collective/Humans#1081): a config allowlist filters the
/// discovered section set, and deactivating a section an active one consumes fails startup.
/// </summary>
/// <remarks>
/// Every case drives off what discovery actually found and what the assemblies actually
/// reference — no section is named here, because naming one would be the pinned list the
/// mechanism exists to avoid.
/// </remarks>
public sealed class SectionActivationTests
{
    private static IReadOnlyList<Assembly> Discovered => SectionDiscoveryExtensions.SectionAssemblies();

    private static IConfiguration Allowlist(params string[] sections) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(sections
                .Select((name, i) => new KeyValuePair<string, string?>($"{SectionActivation.ActiveKey}:{i}", name)))
            .Build();

    [HumansFact]
    public void NoAllowlistActivatesEverySection()
    {
        SectionActivation.Resolve(Discovered, new ConfigurationBuilder().Build())
            .Should().BeNull(because: "the shipped default runs every section that ships");

        Discovered.Should().OnlyContain(a => SectionActivation.IsActive(SectionName(a)));
    }

    [HumansFact]
    public void AllowlistNamingEverySectionIsTheDefaultSet()
    {
        var all = Discovered.Select(SectionName).ToList();

        SectionActivation.Resolve(Discovered, Allowlist([.. all]))
            .Should().BeEquivalentTo(all);
    }

    [HumansFact]
    public void AllowlistIsMatchedCaseInsensitivelyAndCanonicalised()
    {
        // A section that consumes nothing, so the allowlist can hold it alone.
        var leaves = SectionActivation.DependencyGraph(Discovered)
            .Where(e => e.Value.Count == 0)
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        leaves.Should().NotBeEmpty(because: "some section sits at the bottom of the graph");

        SectionActivation.Resolve(Discovered, Allowlist(leaves[0].ToUpperInvariant()))
            .Should().Contain(leaves[0]);
    }

    [HumansFact]
    public void AllowlistNamingSomethingThatIsNotASectionFails()
    {
        var act = () => SectionActivation.Resolve(Discovered, Allowlist("NotASection"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NotASection*", because: "a typo must not silently drop a section");
    }

    [HumansFact]
    public void DeactivatingAConsumedSectionFailsAndNamesTheDependents()
    {
        var (dependent, dependency) = FirstEdge();

        var act = () => SectionActivation.Resolve(Discovered, Allowlist(dependent));

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
