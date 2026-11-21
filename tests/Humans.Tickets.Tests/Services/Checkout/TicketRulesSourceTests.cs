using AwesomeAssertions;
using Humans.Testing;
using Humans.Tickets.Services.Checkout;

namespace Humans.Tickets.Tests.Services.Checkout;

public class TicketRulesSourceTests
{
    [HumansFact]
    public void The_suffix_less_file_is_spanish_and_names_the_set()
    {
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/NORMAS-en.md", "R/NORMAS-DE.md", "R/notes-en.md", "R/logo.png"])
            .Should().BeEquivalentTo([("R/NORMAS.md", "es"), ("R/NORMAS-en.md", "en"), ("R/NORMAS-DE.md", "de")]);
    }

    [HumansFact]
    public void A_second_suffix_less_file_or_a_missing_language_gives_no_rules()
    {
        GitHubTicketRulesSource.RulesFiles(["R/README.md", "R/NORMAS.md", "R/NORMAS-en.md"]).Should().BeNull();
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/OTHER-en.md"]).Should().BeNull();
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS-en.md"]).Should().BeNull();
    }
}
