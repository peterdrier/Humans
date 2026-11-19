using System.Reflection;
using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// The cross-section help invariants no single section can see, over the real discovered contributions. Derived,
/// never a literal roster of keys: each section pins its own keys in its <c>SectionHelpTests</c>.
/// </summary>
public class SectionHelpCorpusTests
{
    private static IReadOnlyList<ISectionHelp> Contributions() =>
        SectionDiscoveryExtensions.DiscoverImplementations<ISectionHelp>();

    private static IReadOnlyList<SectionHelpEntry> Entries() =>
        [.. Contributions().SelectMany(c => c.HelpEntries).OrderBy(e => e.Order)];

    /// <summary>
    /// A <c>Docs/help/</c> file with no entry is content nothing renders; an entry with no file is an empty help modal.
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
    /// A key is what <c>&lt;vc:access-matrix section="..."&gt;</c> takes and what the preload prints as a heading, so
    /// a shared key is ambiguous. A shared order would leave the sequence to DI order.
    /// </summary>
    [HumansFact]
    public void Keys_and_corpus_positions_are_claimed_by_one_entry_each()
    {
        var entries = Entries();

        entries.Select(e => e.Key).Should().OnlyHaveUniqueItems();
        entries.Select(e => e.Order).Should().OnlyHaveUniqueItems();
    }

    /// <summary>A renamed file or a missing EmbeddedResource item would ship an empty help modal.</summary>
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
    /// The model takes glossary headings literally as <c>fetch_section_guide</c> arguments ("## Profile Glossary"
    /// produced dead-end lookups for section="Profile"), so every key must resolve.
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
    /// The preload folds only verbatim-identical term rows, so shared vocabulary defined in different words reaches the
    /// agent twice. "Board" is off the list: two pages word it differently, a content call not a code one.
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

    /// <summary>Read back off the resource names <see cref="SectionHelpEntry.FromEmbeddedMarkdown"/> looks up.</summary>
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
