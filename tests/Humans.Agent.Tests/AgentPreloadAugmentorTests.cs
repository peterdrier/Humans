using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Agent.Contracts;
using Humans.Base.Models;
using Humans.Agent.Services.Preload;

namespace Humans.Agent.Tests;

/// <summary>
/// The FAQ block is preloaded every turn and exists specifically to fix the
/// answers the production agent got wrong (ticket transfer and shift withdrawal
/// are self-service, not admin-only). These pin the load-bearing facts.
/// </summary>
public class AgentPreloadAugmentorTests
{
    private static string Faq() => Augmentor().BuildFaqMarkdown();

    /// <summary>
    /// The augmentor renders whatever <see cref="ISectionAccessMatrix"/> contributions DI hands
    /// it, so the matrix tests below supply their own rows: the real ones are pinned in each
    /// owning section's test project, and asserting them here would need Agent to reference
    /// ten sections.
    /// </summary>
    private static AgentPreloadAugmentor Augmentor(params ISectionAccessMatrix[] accessMatrices) =>
        new(accessMatrices);

    private sealed class StubAccessMatrix(params AccessMatrixData[] matrices) : ISectionAccessMatrix
    {
        public IReadOnlyList<AccessMatrixData> AccessMatrices => matrices;
    }

    [HumansFact]
    public void Faq_points_to_self_service_ticket_transfer()
    {
        var faq = Faq();
        faq.Should().Contain("/Tickets/Transfers");
        faq.Should().Contain("tickets@nobodies.team");
    }

    [HumansFact]
    public void Faq_explains_self_service_shift_withdrawal()
    {
        var faq = Faq();
        faq.Should().Contain("/Shifts/Mine");
        faq.Should().Contain("Bail");
    }

    [HumansFact]
    public void Faq_covers_the_recurring_ticket_and_profile_questions()
    {
        var faq = Faq();
        faq.Should().Contain("early-entry");                 // early-entry-for-shifts policy
        faq.Should().Contain("/Profile/Me/Emails");          // bought-under-other-email + change email
        faq.Should().Contain("/Profile/Me/Privacy");         // delete account / data export
        faq.Should().Contain("https://nobodies.team/");      // external comms channels
    }

    [HumansFact]
    public void Glossaries_define_Human_exactly_once()
    {
        var glossaries = Augmentor().BuildGlossariesMarkdown();
        glossaries.Split('\n').Count(l => l.StartsWith("| **Human** |", StringComparison.Ordinal)).Should().Be(1);
    }

    [HumansFact]
    public void Glossaries_keep_every_term_and_definition()
    {
        var glossaries = Augmentor().BuildGlossariesMarkdown();
        foreach (var (_, body) in SectionHelpContent.AllGlossaries())
        {
            foreach (var row in body.Split('\n').Select(l => l.TrimEnd()).Where(l => l.StartsWith("| **", StringComparison.Ordinal)))
            {
                glossaries.Should().Contain(row);
            }
        }
    }

    /// <summary>
    /// The glossary block is the only place the corpus prints something that looks like a
    /// <c>fetch_section_guide</c> argument, and the model takes it literally: "## Profile Glossary"
    /// produced seven dead-end lookups for section="Profile" (nobodies-collective/Humans#949).
    /// Every heading emitted here must be a key the reader can actually serve.
    /// </summary>
    [HumansFact]
    public void Every_glossary_heading_is_a_fetchable_section_key()
    {
        var glossaries = Augmentor().BuildGlossariesMarkdown();

        var headings = glossaries.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal) && l.EndsWith(" Glossary", StringComparison.Ordinal))
            .Select(l => l["## ".Length..^" Glossary".Length])
            .ToList();

        headings.Should().NotBeEmpty();
        glossaries.Should().Contain("fetch_section_guide", "the block must say what the headings are for");
        foreach (var heading in headings)
        {
            // Asserted against the key table itself rather than through AgentSectionDocReader,
            // which is internal to Humans.Agent since the section's G5 move. The reader resolves
            // the key through exactly this table before it fetches anything, so the check is the
            // same one, minus a stub GitHub source.
            AgentSectionKeys.TryResolve(heading, out _).Should().BeTrue(
                $"glossary heading '{heading}' must be a fetch_section_guide key");
        }
    }

    /// <summary>
    /// Regrouping glossaries under section keys puts pages that share a key next to each other,
    /// and several define the same term differently ("Barrio Lead" three ways across the
    /// city-planning pages). Folding them into one table would hand the agent competing
    /// definitions of the same term with no way to tell them apart — each page keeps its own table.
    /// </summary>
    [HumansFact]
    public void No_glossary_table_defines_the_same_term_twice()
    {
        var terms = new List<string>();
        foreach (var line in Augmentor().BuildGlossariesMarkdown().Split('\n').Select(l => l.TrimEnd()))
        {
            if (line.StartsWith("| Term |", StringComparison.Ordinal))
            {
                terms.Clear(); // a new table starts
            }
            else if (line.StartsWith("| **", StringComparison.Ordinal))
            {
                terms.Add(line[..line.IndexOf('|', 2)]);
                terms.Should().OnlyHaveUniqueItems();
            }
        }
        terms.Should().NotBeEmpty();
    }

    /// <summary>Serves any key the reader is willing to resolve, so the assertion is about key resolution only.</summary>
    private sealed class AnySectionSource : IGuideContentSource
    {
        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}");

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<string>, bool)>(([], true));
    }

    /// <summary>A matrix with one Limited row and one all-Denied row, so both branches are exercised.</summary>
    private static AccessMatrixData SampleMatrix(string key, string name, int order) => new()
    {
        Key = key,
        SectionName = name,
        Order = order,
        Roles = ["Volunteer", "Coordinator", "Admin"],
        Features =
        [
            AccessMatrixFeature.Of("Browse things", ("Volunteer", AccessLevel.Allowed), ("Coordinator", AccessLevel.Allowed), ("Admin", AccessLevel.Allowed)),
            AccessMatrixFeature.Of("Join things", ("Volunteer", AccessLevel.Allowed), ("Coordinator", AccessLevel.Allowed), ("Admin", AccessLevel.Allowed)),
            AccessMatrixFeature.Of("Edit things", ("Volunteer", AccessLevel.Limited), ("Coordinator", AccessLevel.Allowed), ("Admin", AccessLevel.Allowed)),
            AccessMatrixFeature.Of("Delete things", ("Volunteer", AccessLevel.Denied), ("Coordinator", AccessLevel.Denied), ("Admin", AccessLevel.Denied)),
        ]
    };

    [HumansFact]
    public void AccessMatrix_keeps_every_allowed_and_limited_role_fact_grouped_by_section()
    {
        var contributed = SampleMatrix("Things", "Things", 10);
        var matrix = Augmentor(new StubAccessMatrix(contributed)).BuildAccessMatrixMarkdown();

        matrix.Should().Contain($"## {contributed.SectionName}");
        foreach (var feature in contributed.Features)
        {
            var roles = feature.RoleAccess
                .Where(kv => kv.Value != AccessLevel.Denied)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value == AccessLevel.Limited ? kv.Key + " (limited)" : kv.Key)
                .ToList();
            if (roles.Count == 0)
            {
                matrix.Should().NotContain(feature.Name, "a feature no role may use is not a fact worth preloading");
                continue;
            }
            matrix.Split('\n').Should().Contain(l =>
                l.StartsWith("- ", StringComparison.Ordinal) &&
                l.Contains($"**{string.Join(", ", roles)}**", StringComparison.Ordinal) &&
                l.Contains(feature.Name, StringComparison.Ordinal));
        }
    }

    [HumansFact]
    public void AccessMatrix_collapses_features_sharing_a_role_set_onto_one_line()
    {
        var matrix = Augmentor(new StubAccessMatrix(SampleMatrix("Things", "Things", 10))).BuildAccessMatrixMarkdown();
        matrix.Split('\n').Should().Contain(l =>
            l.Contains("Browse things", StringComparison.Ordinal) &&
            l.Contains("Join things", StringComparison.Ordinal));
    }

    /// <summary>
    /// The corpus is whatever DI discovered, not a list Base or the Agent keeps: every
    /// contribution is rendered, and the flat order is the one the rows declare — several
    /// sections' entries interleave, so registration order would reshuffle the corpus.
    /// </summary>
    [HumansFact]
    public void AccessMatrix_renders_every_contribution_in_declared_row_order()
    {
        var matrix = Augmentor(
                new StubAccessMatrix(SampleMatrix("Second", "Second", 20), SampleMatrix("Fourth", "Fourth", 40)),
                new StubAccessMatrix(SampleMatrix("Third", "Third", 30), SampleMatrix("First", "First", 10)))
            .BuildAccessMatrixMarkdown();

        var headings = matrix.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .ToList();

        headings.Should().Equal("## First", "## Second", "## Third", "## Fourth");
    }
}
