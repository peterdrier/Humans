using AwesomeAssertions;
using Humans.Web.Models;
using Humans.Web.Services.Agent;

namespace Humans.Web.Tests.Services;

/// <summary>
/// The FAQ block is preloaded every turn and exists specifically to fix the
/// answers the production agent got wrong (ticket transfer and shift withdrawal
/// are self-service, not admin-only). These pin the load-bearing facts.
/// </summary>
public class AgentPreloadAugmentorTests
{
    private static string Faq() => new AgentPreloadAugmentor().BuildFaqMarkdown();

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
        var glossaries = new AgentPreloadAugmentor().BuildGlossariesMarkdown();
        glossaries.Split('\n').Count(l => l.StartsWith("| **Human** |", StringComparison.Ordinal)).Should().Be(1);
    }

    [HumansFact]
    public void Glossaries_keep_every_term_and_definition()
    {
        var glossaries = new AgentPreloadAugmentor().BuildGlossariesMarkdown();
        foreach (var (_, body) in SectionHelpContent.AllGlossaries())
        {
            foreach (var row in body.Split('\n').Select(l => l.TrimEnd()).Where(l => l.StartsWith("| **", StringComparison.Ordinal)))
            {
                glossaries.Should().Contain(row);
            }
        }
    }

    [HumansFact]
    public void AccessMatrix_keeps_every_allowed_role_fact_grouped_by_section()
    {
        var matrix = new AgentPreloadAugmentor().BuildAccessMatrixMarkdown();
        foreach (var section in AccessMatrixDefinitions.Sections.Values)
        {
            matrix.Should().Contain($"## {section.SectionName}");
            foreach (var feature in section.Features)
            {
                var allowed = feature.RoleAccess.Where(kv => kv.Value == AccessLevel.Allowed).Select(kv => kv.Key).ToList();
                if (allowed.Count == 0)
                {
                    continue;
                }
                matrix.Split('\n').Should().Contain(l =>
                    l.StartsWith("- ", StringComparison.Ordinal) &&
                    l.Contains($"**{string.Join(", ", allowed)}**", StringComparison.Ordinal) &&
                    l.Contains(feature.Name, StringComparison.Ordinal));
            }
        }
    }
}
