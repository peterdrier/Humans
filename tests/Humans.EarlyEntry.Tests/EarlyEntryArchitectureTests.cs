using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.Extensions.Localization;

namespace Humans.EarlyEntry.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Early Entry
/// (nobodies-collective/Humans#866, G5 lane 4b-2b).
/// </summary>
public class EarlyEntryArchitectureTests
{
    [HumansFact]
    public void OnlySectionAndContractsArePublic()
    {
        // "Public means Section, <Section>Resource or Contracts/" (design §15 step 5),
        // enforced at build time by HUM0034. There is no resource marker — the roster page's
        // copy is inline English — and no migrations, because the section owns no tables.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
        [
            "Humans.EarlyEntry.Contracts.EarlyEntryGrant",
            "Humans.EarlyEntry.Contracts.EarlyEntryRosterRow",
            "Humans.EarlyEntry.Contracts.IEarlyEntryInvalidator",
            "Humans.EarlyEntry.Contracts.IEarlyEntryProvider",
            "Humans.EarlyEntry.Contracts.IEarlyEntryService",
            "Humans.EarlyEntry.Contracts.UserEarlyEntry",
            "Humans.EarlyEntry.Section",
        ]);
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        // Shell registers SectionControllerFeatureProvider, which relaxes MVC's IsPublic check
        // for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        controllers.Should().HaveCount(1);
        controllers.Should().OnlyContain(t => !t.IsPublic);
    }

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // The section ships no Resources/ folder because every string on the roster page is
        // inline English (§15 step 3b — Gate's shape). Asserted structurally so the day
        // someone adds copy, the build tells them to carve a resource set first rather than
        // silently binding SharedResource from another assembly.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters()))
                .Where(p => p.ParameterType.IsGenericType
                            && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
                .Select(_ => t.FullName ?? t.Name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Early Entry has no resource set; a localizer binding here would read "
                   + "another assembly's set and render raw keys");
    }

    [HumansFact]
    public void SectionAssemblyDoesNotReferenceEntityFrameworkCore()
    {
        // The section owns no tables, has no DbContext, takes no Humans.Infrastructure
        // reference and injects no repository — so it cannot *name* a DbContext. That is the
        // orchestrator clause in peters-hard-rules.md made mechanical.
        typeof(Section).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should().NotContain("Microsoft.EntityFrameworkCore",
                because: "Early Entry derives every grant from its providers and owns no tables");
    }

    [HumansFact]
    public void OrchestratorInjectsOnlyTheProviderFanout()
    {
        // The hard rules' orchestrator clause: "Some services are orchestrators, organizing
        // calls to multiple services. These should not call repositories." The fan-out is the
        // whole dependency list — anything else here would be the section growing a data path.
        var paramTypes = typeof(EarlyEntryService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().BeEquivalentTo([typeof(IEnumerable<IEarlyEntryProvider>)]);
    }

    [HumansFact]
    public void CachingDecoratorInjectsNoInnerServiceDirectly()
    {
        // peters-hard-rules.md: a CachingDecorator may not call a repository, and must reach
        // the inner service through the interface. Here the Singleton decorator resolves the
        // Scoped inner service per call off the keyed registration, so its constructor takes a
        // scope factory and a logger and nothing else — injecting IEarlyEntryService directly
        // would self-resolve onto the decorator's own unkeyed registration.
        var paramTypes = typeof(CachingEarlyEntryService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().NotContain(typeof(IEarlyEntryService));
        paramTypes.Should().NotContain(
            t => t.Name.EndsWith("Repository", StringComparison.Ordinal));
    }
}
