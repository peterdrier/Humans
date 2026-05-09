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
        var snapshot = new AgentUserSnapshot(
            UserId: Guid.NewGuid(),
            DisplayName: "Felipe García",
            PreferredLocale: "es",
            Tier: "Volunteer",
            IsApproved: true,
            RoleAssignments: new[] { ("TeamsAdmin", "2027-12-31") },
            Teams: new[]
            {
                new TeamMembership("Volunteers", TeamMemberRole.Member),
                new TeamMembership("Tech", TeamMemberRole.Coordinator)
            },
            PendingConsentDocs: new[] { "Privacy Policy" },
            OpenTicketIds: Array.Empty<Guid>(),
            OpenFeedbackIds: Array.Empty<Guid>(),
            UpcomingShifts: Array.Empty<UpcomingShiftEntry>());

        var assembler = new AgentPromptAssembler();
        var tail = assembler.BuildUserContextTail(snapshot);

        tail.Should().Contain("Felipe García");
        tail.Should().Contain("Locale: es");
        tail.Should().Contain("TeamsAdmin");
        tail.Should().Contain("Privacy Policy");
    }

    [HumansFact]
    public void BuildUserContextTail_renders_team_with_role_per_team()
    {
        var snapshot = new AgentUserSnapshot(
            UserId: Guid.NewGuid(),
            DisplayName: "Volunteer",
            PreferredLocale: "es",
            Tier: "Volunteer",
            IsApproved: true,
            RoleAssignments: Array.Empty<(string, string)>(),
            Teams: new[]
            {
                new TeamMembership("Build", TeamMemberRole.Coordinator),
                new TeamMembership("Cantina", TeamMemberRole.Member)
            },
            PendingConsentDocs: Array.Empty<string>(),
            OpenTicketIds: Array.Empty<Guid>(),
            OpenFeedbackIds: Array.Empty<Guid>(),
            UpcomingShifts: Array.Empty<UpcomingShiftEntry>());

        var assembler = new AgentPromptAssembler();
        var tail = assembler.BuildUserContextTail(snapshot);

        tail.Should().Contain("Build (Coordinator)");
        tail.Should().Contain("Cantina (Member)");
    }

    [HumansFact]
    public void BuildUserContextTail_renders_upcoming_shift_block_with_date_range_and_day_count()
    {
        var blockKey = Guid.NewGuid();
        var snapshot = MakeSnapshot(
            new UpcomingShiftEntry(
                Key: blockKey,
                Label: "Cantina build",
                StartDate: new LocalDate(2026, 7, 1),
                EndDate: new LocalDate(2026, 7, 7),
                DayCount: 7,
                Status: SignupStatus.Confirmed));

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        tail.Should().Contain("UpcomingShifts:");
        tail.Should().Contain("2026-07-01 to 2026-07-07");
        tail.Should().Contain("Cantina build");
        tail.Should().Contain("(Confirmed, 7 days)");
    }

    [HumansFact]
    public void BuildUserContextTail_renders_upcoming_shift_singleton_with_single_date()
    {
        var snapshot = MakeSnapshot(
            new UpcomingShiftEntry(
                Key: Guid.NewGuid(),
                Label: "Setup crew",
                StartDate: new LocalDate(2026, 7, 15),
                EndDate: new LocalDate(2026, 7, 15),
                DayCount: 1,
                Status: SignupStatus.Pending));

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        tail.Should().Contain("2026-07-15 — Setup crew (Pending)");
        // Singletons must NOT include "(Pending, 1 days)" — only the block path renders day count.
        tail.Should().NotContain("1 days");
    }

    [HumansFact]
    public void BuildUserContextTail_omits_upcoming_shifts_section_when_empty()
    {
        var snapshot = MakeSnapshot();
        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);
        tail.Should().NotContain("UpcomingShifts");
    }

    [HumansFact]
    public void BuildUserContextTail_sorts_upcoming_shifts_by_start_date_at_render_time()
    {
        // Service layer returns unsorted; the rendering layer is responsible for display order.
        var snapshot = MakeSnapshot(
            new UpcomingShiftEntry(Guid.NewGuid(), "Later", new LocalDate(2026, 7, 15), new LocalDate(2026, 7, 15), 1, SignupStatus.Confirmed),
            new UpcomingShiftEntry(Guid.NewGuid(), "Earlier", new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 1), 1, SignupStatus.Confirmed));

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        var earlierIdx = tail.IndexOf("Earlier", StringComparison.Ordinal);
        var laterIdx = tail.IndexOf("Later", StringComparison.Ordinal);
        earlierIdx.Should().BeGreaterThan(0);
        laterIdx.Should().BeGreaterThan(earlierIdx);
    }

    [HumansFact]
    public void BuildUserContextTail_sorts_teams_alphabetically_at_render_time()
    {
        var snapshot = new AgentUserSnapshot(
            UserId: Guid.NewGuid(),
            DisplayName: "Test",
            PreferredLocale: "es",
            Tier: "Volunteer",
            IsApproved: true,
            RoleAssignments: Array.Empty<(string, string)>(),
            Teams: new[]
            {
                new TeamMembership("Zebra", TeamMemberRole.Member),
                new TeamMembership("Apple", TeamMemberRole.Coordinator),
            },
            PendingConsentDocs: Array.Empty<string>(),
            OpenTicketIds: Array.Empty<Guid>(),
            OpenFeedbackIds: Array.Empty<Guid>(),
            UpcomingShifts: Array.Empty<UpcomingShiftEntry>());

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        var appleIdx = tail.IndexOf("Apple", StringComparison.Ordinal);
        var zebraIdx = tail.IndexOf("Zebra", StringComparison.Ordinal);
        appleIdx.Should().BeGreaterThan(0);
        zebraIdx.Should().BeGreaterThan(appleIdx);
    }

    [HumansFact]
    public void BuildUserContextTail_renders_shift_key_so_agent_can_call_get_shift_details()
    {
        var key = Guid.NewGuid();
        var snapshot = MakeSnapshot(
            new UpcomingShiftEntry(key, "Setup crew", new LocalDate(2026, 7, 15), new LocalDate(2026, 7, 15), 1, SignupStatus.Pending));

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        tail.Should().Contain($"[{key}]");
    }

    [HumansFact]
    public void BuildUserContextTail_caps_upcoming_shifts_with_overflow_marker()
    {
        var entries = Enumerable.Range(0, 12)
            .Select(i => new UpcomingShiftEntry(
                Key: Guid.NewGuid(),
                Label: "Rota " + i,
                StartDate: new LocalDate(2026, 7, 1).PlusDays(i),
                EndDate: new LocalDate(2026, 7, 1).PlusDays(i),
                DayCount: 1,
                Status: SignupStatus.Confirmed))
            .ToArray();

        var snapshot = MakeSnapshot(entries);

        var tail = new AgentPromptAssembler().BuildUserContextTail(snapshot);

        // First 10 entries rendered.
        tail.Should().Contain("Rota 0");
        tail.Should().Contain("Rota 9");
        // Entries beyond cap not in body.
        tail.Should().NotContain("Rota 10");
        tail.Should().NotContain("Rota 11");
        // Overflow marker present.
        tail.Should().Contain("+2 more");
    }

    private static AgentUserSnapshot MakeSnapshot(params UpcomingShiftEntry[] upcoming) =>
        new(
            UserId: Guid.NewGuid(),
            DisplayName: "Test",
            PreferredLocale: "es",
            Tier: "Volunteer",
            IsApproved: true,
            RoleAssignments: Array.Empty<(string, string)>(),
            Teams: Array.Empty<TeamMembership>(),
            PendingConsentDocs: Array.Empty<string>(),
            OpenTicketIds: Array.Empty<Guid>(),
            OpenFeedbackIds: Array.Empty<Guid>(),
            UpcomingShifts: upcoming);
}
