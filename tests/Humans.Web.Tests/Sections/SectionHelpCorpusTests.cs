using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// The section-help corpus as the app actually composes it. The content moved out of Base into
/// each owning section (<see cref="ISectionHelp"/>), so the invariants that span sections — the
/// key set, one definition per shared term, headings the agent can actually fetch — no longer
/// have a section that can see them. They live here, over the real discovered contributions.
/// </summary>
public class SectionHelpCorpusTests
{
    private static IReadOnlyList<SectionHelpEntry> Entries() =>
        [.. SectionDiscoveryExtensions.DiscoverImplementations<ISectionHelp>()
            .SelectMany(c => c.HelpEntries)
            .OrderBy(e => e.Order)];

    [HumansFact]
    public void Every_help_page_is_contributed_exactly_once()
    {
        var entries = Entries();

        entries.Select(e => e.Key).Should().Equal(
            "Teams", "Profile", "Admin", "Shifts", "Camps", "Governance",
            "OnboardingReview", "Board", "Tickets", "ContainerMap",
            "CityPlanningOverview", "CityPlanningBarrioMap");
        entries.Select(e => e.Order).Should().OnlyHaveUniqueItems(
            "the corpus order is the entries' to declare, and a tie would resolve on DI order");
    }

    /// <summary>
    /// Every entry's markdown resolves out of its section's embedded resources — a renamed file
    /// or a csproj that forgot the EmbeddedResource item would otherwise ship as a silently
    /// empty help modal.
    /// </summary>
    [HumansFact]
    public void Every_entry_carries_both_its_guide_and_its_glossary()
    {
        foreach (var entry in Entries())
        {
            entry.Guide.Should().NotBeNullOrWhiteSpace(entry.Key);
            entry.Glossary.Should().NotBeNullOrWhiteSpace(entry.Key);
        }
    }

    /// <summary>
    /// The glossary block is the only place the preload corpus prints something shaped like a
    /// <c>fetch_section_guide</c> argument, and the model takes it literally: "## Profile Glossary"
    /// produced seven dead-end lookups for section="Profile" (nobodies-collective/Humans#949).
    /// Every key a section contributes must resolve, whatever it calls its page.
    /// </summary>
    [HumansFact]
    public void Every_contributed_help_key_is_a_fetchable_section_key()
    {
        foreach (var entry in Entries())
        {
            AgentSectionKeys.TryResolve(entry.Key, out _).Should().BeTrue(
                $"help key '{entry.Key}' must be a fetch_section_guide key");
        }
    }

    /// <summary>
    /// The preload folds a term row that appears verbatim on several pages into one shared
    /// table. A term two sections define in different words is not folded and the agent meets
    /// two definitions of it — fine for a page-specific term like "Barrio Lead", wrong for the
    /// vocabulary every section shares. "Board" is deliberately off this list: two pages
    /// word it differently today, which predates the seam and is a content call, not a code one.
    /// </summary>
    [HumansFact]
    public void The_shared_vocabulary_is_defined_the_same_way_everywhere()
    {
        foreach (var term in new[] { "Human", "Coordinator" })
        {
            var prefix = $"| **{term}** |";
            Entries()
                .SelectMany(e => e.Glossary!.Split('\n'))
                .Select(l => l.TrimEnd())
                .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Should().HaveCountLessThanOrEqualTo(1, $"'{term}' must read the same in every glossary");
        }
    }
}
