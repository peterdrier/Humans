using System.Reflection;
using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// The section-help corpus as the app actually composes it. The content moved out of Base into
/// each owning section (<see cref="ISectionHelp"/>), so the invariants that span sections — one
/// contributor per help page, one definition per shared term, headings the agent can actually
/// fetch — no longer have a section that can see them. They live here, over the real discovered
/// contributions.
/// </summary>
/// <remarks>
/// Deliberately derived, never a literal roster of keys: a hub-side list of which sections have
/// help content is the exact coupling the seam removed, and it would restate composition rather
/// than detect a defect in it. Each section pins its own keys and orders in its own
/// <c>SectionHelpTests</c>; what is only visible from here is the correspondence between them.
/// </remarks>
public class SectionHelpCorpusTests
{
    private static IReadOnlyList<ISectionHelp> Contributions() =>
        SectionDiscoveryExtensions.DiscoverImplementations<ISectionHelp>();

    private static IReadOnlyList<SectionHelpEntry> Entries() =>
        [.. Contributions().SelectMany(c => c.HelpEntries).OrderBy(e => e.Order)];

    /// <summary>
    /// The two authoritative sources — the markdown each section embeds and the entries it
    /// contributes — must name the same pages. A file added under <c>Docs/help/</c> with no
    /// <c>ISectionHelp</c> entry is content nothing renders; an entry with no file is a help
    /// modal that ships empty. Both are silent today, and neither is visible from one section.
    /// </summary>
    [HumansFact]
    public void Every_shipped_help_page_is_contributed_exactly_once()
    {
        var contributed = Contributions()
            .SelectMany(c => c.HelpEntries.Select(e => (Assembly: AssemblyName(c.GetType().Assembly), e.Key)))
            .OrderBy(x => x.Assembly, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        var shipped = SectionDiscoveryExtensions.ActiveSectionAssemblies()
            .SelectMany(a => HelpPageKeys(a).Select(key => (Assembly: AssemblyName(a), Key: key)))
            .OrderBy(x => x.Assembly, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        contributed.Should().Equal(shipped,
            "every embedded Docs/help markdown is claimed by exactly one ISectionHelp entry, and vice versa");
    }

    /// <summary>
    /// A key is what <c>&lt;vc:access-matrix section="..."&gt;</c> is called with and what the
    /// preload prints as a heading, so two sections claiming one key makes both ambiguous. Orders
    /// collide the same way: the corpus sequence would then resolve on DI order.
    /// </summary>
    [HumansFact]
    public void Keys_and_corpus_positions_are_claimed_by_one_entry_each()
    {
        var entries = Entries();

        entries.Select(e => e.Key).Should().OnlyHaveUniqueItems();
        entries.Select(e => e.Order).Should().OnlyHaveUniqueItems();
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

    private static string AssemblyName(Assembly assembly) => assembly.GetName().Name!;

    /// <summary>
    /// The help page keys an assembly ships markdown for, read back off the embedded resource
    /// names <see cref="SectionHelpEntry.FromEmbeddedMarkdown"/> looks them up by.
    /// </summary>
    private static IEnumerable<string> HelpPageKeys(Assembly assembly)
    {
        var prefix = FormattableString.Invariant($"{AssemblyName(assembly)}.Docs.help.");
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .Where(stem => stem.EndsWith(".guide.md", StringComparison.Ordinal)
                        || stem.EndsWith(".glossary.md", StringComparison.Ordinal))
            .Select(stem => stem[..stem.IndexOf('.', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal);
    }
}
