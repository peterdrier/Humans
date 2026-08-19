using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
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

    /// <summary>The Shell assembly, which always runs and consumes sections of its own.</summary>
    private static Assembly Shell => typeof(SectionActivation).Assembly;

    private static IConfiguration Allowlist(params string[] sections) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(sections
                .Select((name, i) => new KeyValuePair<string, string?>($"{SectionActivation.ActiveKey}:{i}", name)))
            .Build();

    [HumansFact]
    public void NoAllowlistActivatesEverySection()
    {
        SectionActivation.Resolve(Discovered, Shell, new ConfigurationBuilder().Build())
            .Should().BeNull(because: "the shipped default runs every section that ships");

        new SectionAssemblySnapshot(Discovered, activeSections: null)
            .Active.Should().BeEquivalentTo(Discovered);
    }

    [HumansFact]
    public void AllowlistNamingEverySectionIsTheDefaultSet()
    {
        var all = Discovered.Select(SectionName).ToList();

        SectionActivation.Resolve(Discovered, Shell, Allowlist([.. all]))
            .Should().BeEquivalentTo(all);
    }

    [HumansFact]
    public void AllowlistIsMatchedCaseInsensitivelyAndCanonicalised()
    {
        var all = Discovered.Select(SectionName).ToList();

        SectionActivation.Resolve(Discovered, Shell, Allowlist([.. all.Select(n => n.ToUpperInvariant())]))
            .Should().BeEquivalentTo(all,
                because: "names match case-insensitively and come back spelled as discovery found them");
    }

    [HumansFact]
    public void ExplicitlyEmptyAllowlistIsZeroSectionsNotEverySection()
    {
        // How `"Sections:Active": []` binds: the key is present with an empty value, which
        // is what separates it from the key being absent.
        var empty = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(SectionActivation.ActiveKey, "")])
            .Build();

        var act = () => SectionActivation.Resolve(Discovered, Shell, empty);

        act.Should().Throw<InvalidOperationException>(
            because: "activating nothing leaves Shell running with none of the sections it consumes, "
                     + "where the absent key means every section and resolves to null");
    }

    [HumansFact]
    public void DeactivatingASectionShellItselfConsumesFails()
    {
        var shellDependencies = SectionActivation.ShellDependencies(Discovered, Shell);
        shellDependencies.Should().NotBeEmpty(
            because: "Shell's own controllers name section Contracts types");

        var dropped = shellDependencies[0];
        var allowed = Discovered.Select(SectionName)
            .Where(name => !name.Equals(dropped, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var act = () => SectionActivation.Resolve(Discovered, Shell, Allowlist(allowed));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{dropped}*{SectionName(Shell)}*",
                because: "Shell always runs, so no allowlist may deactivate what Shell consumes");
    }

    [HumansFact]
    public void AllowlistNamingSomethingThatIsNotASectionFails()
    {
        var act = () => SectionActivation.Resolve(Discovered, Shell, Allowlist("NotASection"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NotASection*", because: "a typo must not silently drop a section");
    }

    [HumansFact]
    public void DeactivatingAConsumedSectionFailsAndNamesTheDependents()
    {
        var (dependent, dependency) = FirstEdge();

        var act = () => SectionActivation.Resolve(Discovered, Shell, Allowlist(dependent));

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
