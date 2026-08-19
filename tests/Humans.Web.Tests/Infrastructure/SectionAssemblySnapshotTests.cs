using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Everything a host composes from discovery reads its own snapshot
/// (nobodies-collective/Humans#1081) — the MVC feature providers and the section
/// registrations alike. Several hosts share a process — every <c>WebApplicationFactory</c>
/// in the integration suite builds one — so process-wide activation state would compose one
/// host's sections inside another.
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

    /// <summary>
    /// Registrations come from the host's own snapshot too, not only the MVC providers:
    /// two hosts composing in one process each register only the sections they activated.
    /// </summary>
    [HumansFact]
    public void TwoHostsInOneProcessRegisterOnlyTheirOwnSections()
    {
        var (dropped, assembly) = DeactivatableSectionThatRegisters();

        var kept = Shipped.Select(SectionName)
            .Where(name => !name.Equals(dropped, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var hostOne = Compose(SectionActivation.Resolve(Shipped, Shell, Allowlist(kept)));
        var hostTwo = Compose(activeSections: null);

        RegisteringAssemblies(hostOne).Should().NotContain(assembly,
            because: $"host 1 deactivated {dropped}, so its Register never ran");
        RegisteringAssemblies(hostTwo).Should().Contain(assembly,
            because: "host 2 activated every section and must not inherit host 1's set");
    }

    [HumansFact]
    public void TheDefaultAllowlistActivatesEverySection()
    {
        var snapshot = new SectionAssemblySnapshot(Shipped, activeSections: null);

        Shipped.Should().OnlyContain(a => snapshot.IsActiveSection(a));
        Shipped.Should().NotContain(a => snapshot.IsInactiveSection(a));
    }

    /// <summary>
    /// Sections that can be switched off on their own: not pinned by Shell, and consumed by
    /// no other section, so the allowlist still validates. Derived, never named.
    /// </summary>
    private static IReadOnlyList<string> Deactivatable()
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

        return deactivatable;
    }

    private static string DeactivatableSection() => Deactivatable()[0];

    /// <summary>The first deactivatable section whose <c>Register</c> actually adds something.</summary>
    private static (string Name, Assembly Assembly) DeactivatableSectionThatRegisters()
    {
        var registering = RegisteringAssemblies(Compose(activeSections: null));

        var candidates = Deactivatable()
            .Select(name => (Name: name, Assembly: AssemblyOf(name)))
            .Where(candidate => registering.Contains(candidate.Assembly))
            .ToList();

        candidates.Should().NotBeEmpty(
            because: "a deactivatable section that registers nothing cannot show a registration difference");

        return candidates[0];
    }

    private static Assembly AssemblyOf(string section) =>
        Shipped.Single(a => SectionName(a).Equals(section, StringComparison.OrdinalIgnoreCase));

    private static IServiceCollection Compose(IReadOnlySet<string>? activeSections) =>
        new ServiceCollection().AddDiscoveredSections(
            new SectionAssemblySnapshot(Shipped, activeSections),
            new ConfigurationBuilder().Build());

    /// <summary>
    /// The assemblies a composed collection actually registered from. Keyed descriptors
    /// expose their type under different properties and throw on the unkeyed ones.
    /// </summary>
    private static HashSet<Assembly> RegisteringAssemblies(IServiceCollection services) =>
        [.. services
            .SelectMany(d => new[]
            {
                d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType,
                (d.IsKeyedService ? d.KeyedImplementationInstance : d.ImplementationInstance)?.GetType(),
            })
            .OfType<Type>()
            .Select(t => t.Assembly)];
}
