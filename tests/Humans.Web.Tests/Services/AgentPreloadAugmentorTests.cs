using AwesomeAssertions;
using Humans.Web.Services.Agent;
using Xunit;

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
}
