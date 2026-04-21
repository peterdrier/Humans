using AwesomeAssertions;
using Humans.Application.Models;
using Humans.Infrastructure.Services.Agent;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentPromptAssemblerTests
{
    [Fact]
    public void BuildUserContextTail_includes_display_name_and_locale_header()
    {
        var snapshot = new AgentUserSnapshot(
            UserId: Guid.NewGuid(),
            DisplayName: "Felipe García",
            PreferredLocale: "es",
            Tier: "Volunteer",
            IsApproved: true,
            RoleAssignments: new[] { ("TeamsAdmin", "2027-12-31") },
            Teams: new[] { "Volunteers", "Tech" },
            PendingConsentDocs: new[] { "Privacy Policy" },
            OpenTicketIds: Array.Empty<Guid>(),
            OpenFeedbackIds: Array.Empty<Guid>());

        var assembler = new AgentPromptAssembler();
        var tail = assembler.BuildUserContextTail(snapshot);

        tail.Should().Contain("Felipe García");
        tail.Should().Contain("Locale: es");
        tail.Should().Contain("TeamsAdmin");
        tail.Should().Contain("Privacy Policy");
    }
}
