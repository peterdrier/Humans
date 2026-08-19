using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
using Microsoft.Extensions.Configuration;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// The MVC feature providers read their section sets from a per-host snapshot
/// (nobodies-collective/Humans#1081). Several hosts share a process — every
/// <c>WebApplicationFactory</c> in the integration suite builds one — so a process-wide
/// cache would serve whichever host composed first to all of them.
/// </summary>
public sealed class SectionAssemblySnapshotTests
{
    private static IReadOnlyList<Assembly> Shipped => SectionDiscoveryExtensions.SectionAssemblies();

    private static Assembly Shell => typeof(SectionActivation).Assembly;

    private static string SectionName(Assembly assembly) =>
        assembly.GetName().Name!["Humans.".Length..];

    private static IConfiguration Allowlist(params string[] sections) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(sections
                .Select((name, i) => new KeyValuePair<string, string?>($"{SectionActivation.ActiveKey}:{i}", name)))
            .Build();

    [HumansFact]
    public void TwoHostsInOneProcessEachSeeTheirOwnSections()
    {
        var dropped = DeactivatableSection();
        var assembly = Shipped.Single(a =>
            SectionName(a).Equals(dropped, StringComparison.OrdinalIgnoreCase));

        var kept = Shipped.Select(SectionName)
            .Where(name => !name.Equals(dropped, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Host 1 deactivates it; host 2 takes the default, which is every section.
        var hostOne = new SectionAssemblySnapshot(
            Shipped, SectionActivation.Resolve(Shipped, Shell, Allowlist(kept)));
        var hostTwo = new SectionAssemblySnapshot(
            Shipped, SectionActivation.Resolve(Shipped, Shell, new ConfigurationBuilder().Build()));

        hostOne.IsInactiveSection(assembly).Should().BeTrue(because: $"host 1 deactivated {dropped}");
        hostOne.IsActiveSection(assembly).Should().BeFalse();

        hostTwo.IsInactiveSection(assembly).Should().BeFalse(
            because: "host 2 activated every section and must not inherit host 1's set");
        hostTwo.IsActiveSection(assembly).Should().BeTrue();

        // Composing host 2 leaves host 1's own view of the world alone.
        hostOne.IsInactiveSection(assembly).Should().BeTrue();
    }

    [HumansFact]
    public void TheDefaultAllowlistActivatesEverySection()
    {
        var snapshot = new SectionAssemblySnapshot(Shipped, activeSections: null);

        Shipped.Should().OnlyContain(a => snapshot.IsActiveSection(a));
        Shipped.Should().NotContain(a => snapshot.IsInactiveSection(a));
    }

    /// <summary>
    /// A section that can be switched off on its own: not pinned by Shell, and consumed by
    /// no other section, so the allowlist still validates. Derived, never named.
    /// </summary>
    private static string DeactivatableSection()
    {
        var consumed = SectionActivation.DependencyGraph(Shipped)
            .Values.SelectMany(dependencies => dependencies)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pinnedByShell = SectionActivation.ShellDependencies(Shipped, Shell)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deactivatable = Shipped.Select(SectionName)
            .Where(name => !consumed.Contains(name) && !pinnedByShell.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        deactivatable.Should().NotBeEmpty(
            because: "some section is a leaf Shell does not name, or nothing is deactivatable at all");

        return deactivatable[0];
    }
}
