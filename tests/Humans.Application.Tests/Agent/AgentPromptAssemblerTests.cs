using AwesomeAssertions;
using Humans.Application.Models;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Agent;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentPromptAssemblerTests
{
    [HumansFact]
    public void BuildUserContextTail_includes_display_name_and_locale_header()
    {
        var snapshot = MakeSnapshot(
            teams: new[]
            {
                new TeamMembership("Build", TeamMemberRole.Coordinator),
                new TeamMembership("Cantina", TeamMemberRole.Member)
            },
            pendingConsents: new[] { "Privacy Policy" });

        var assembler = new AgentPromptAssembler();
        var tail = assembler.BuildUserContextTail(snapshot);

        tail.Should().Contain("Felipe García");
        tail.Should().Contain("Locale: es");
        tail.Should().Contain("TeamsAdmin");
        tail.Should().Contain("Privacy Policy");
    }

    [HumansFact]
    public void BuildUserContextTail_renders_team_role_per_team()
    {
        var snapshot = MakeSnapshot(teams: new[]
        {
            new TeamMembership("Build", TeamMemberRole.Coordinator),
            new TeamMembership("Cantina", TeamMemberRole.Member)
        });

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        tail.Should().Contain("Teams:");
        tail.Should().Contain("- Build (Coordinator)");
        tail.Should().Contain("- Cantina (Member)");
    }

    [HumansFact]
    public void BuildUserContextTail_omits_upcoming_shifts_when_empty()
    {
        var snapshot = MakeSnapshot();

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        tail.Should().NotContain("UpcomingShifts:");
    }

    [HumansFact]
    public void BuildUserContextTail_renders_upcoming_shifts_singleton_and_block()
    {
        var blockKey = Guid.NewGuid();
        var singletonKey = Guid.NewGuid();
        var snapshot = MakeSnapshot(upcoming: new[]
        {
            new UpcomingShiftEntry(blockKey, "Cantina build",
                new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 7), 7, SignupStatus.Confirmed),
            new UpcomingShiftEntry(singletonKey, "Setup crew",
                new LocalDate(2026, 7, 15), new LocalDate(2026, 7, 15), 1, SignupStatus.Pending)
        });

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        tail.Should().Contain("UpcomingShifts:");
        tail.Should().Contain("2026-07-01 to 2026-07-07 — Cantina build (Confirmed, 7 days)");
        tail.Should().Contain("2026-07-15 — Setup crew (Pending)");
        // Key is rendered so the agent can pass it to get_shift_details.
        tail.Should().Contain(blockKey.ToString("D"));
        tail.Should().Contain(singletonKey.ToString("D"));
    }

    [HumansFact]
    public void BuildUserContextTail_caps_upcoming_shifts_with_overflow_line()
    {
        var entries = Enumerable.Range(0, 13)
            .Select(i => new UpcomingShiftEntry(
                Guid.NewGuid(),
                $"Rota {i}",
                new LocalDate(2026, 7, 1).PlusDays(i),
                new LocalDate(2026, 7, 1).PlusDays(i),
                1,
                SignupStatus.Confirmed))
            .ToList();
        var snapshot = MakeSnapshot(upcoming: entries);

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        tail.Should().Contain("Rota 0");
        tail.Should().Contain("Rota 9");
        tail.Should().NotContain("Rota 10");
        tail.Should().Contain("+3 more");
    }

    [HumansFact]
    public void BuildToolDefinitions_includes_get_shift_details()
    {
        var defs = new AgentPromptAssembler().BuildToolDefinitions();
        defs.Should().Contain(d => string.Equals(d.Name, "get_shift_details", StringComparison.Ordinal));
        var shiftTool = defs.Single(d => string.Equals(d.Name, "get_shift_details", StringComparison.Ordinal));
        shiftTool.JsonSchema.Should().Contain("\"shiftId\"");
        shiftTool.JsonSchema.Should().Contain("\"format\":\"uuid\"");
    }

    [HumansFact]
    public void BuildSystemPrompt_mentions_get_shift_details_for_shift_specifics()
    {
        var prompt = new AgentPromptAssembler().BuildSystemPrompt(preloadCorpus: "");

        prompt.Should().Contain("get_shift_details");
    }

    private static AgentUserSnapshot MakeSnapshot(
        IReadOnlyList<TeamMembership>? teams = null,
        IReadOnlyList<string>? pendingConsents = null,
        IReadOnlyList<UpcomingShiftEntry>? upcoming = null) =>
        new(
            UserId: Guid.NewGuid(),
            DisplayName: "Felipe García",
            PreferredLocale: "es",
            Tier: "Volunteer",
            IsApproved: true,
            RoleAssignments: new[] { ("TeamsAdmin", "2027-12-31") },
            Teams: teams ?? Array.Empty<TeamMembership>(),
            PendingConsentDocs: pendingConsents ?? Array.Empty<string>(),
            OpenTicketIds: Array.Empty<Guid>(),
            OpenFeedbackIds: Array.Empty<Guid>(),
            UpcomingShifts: upcoming ?? Array.Empty<UpcomingShiftEntry>());
}
