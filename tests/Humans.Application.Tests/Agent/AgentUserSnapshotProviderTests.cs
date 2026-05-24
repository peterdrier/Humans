using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Models;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Agent;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Agent;

public class AgentUserSnapshotProviderTests
{
    [HumansFact]
    public async Task LoadAsync_collapses_block_signups_into_one_entry_with_date_range_and_day_count()
    {
        var userId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var ev = MakeEventSettings();
        var rota = new Rota { Id = Guid.NewGuid(), Name = "Cantina build" };
        var signups = Enumerable.Range(0, 7)
            .Select(i => MakeSignup(userId, blockId, MakeShift(rota, dayOffset: -10 + i, isAllDay: true), SignupStatus.Confirmed))
            .ToList();

        var provider = MakeProvider(userId, ev, signups);

        var snapshot = await provider.LoadAsync(userId, CancellationToken.None);

        snapshot.UpcomingShifts.Should().HaveCount(1);
        var entry = snapshot.UpcomingShifts[0];
        entry.Key.Should().Be(blockId);
        entry.Label.Should().Be("Cantina build");
        entry.DayCount.Should().Be(7);
        entry.StartDate.Should().Be(new LocalDate(2026, 6, 21));
        entry.EndDate.Should().Be(new LocalDate(2026, 6, 27));
        entry.Status.Should().Be(SignupStatus.Confirmed);
    }

    [HumansFact]
    public async Task LoadAsync_singletons_pass_through_with_start_equal_end()
    {
        var userId = Guid.NewGuid();
        var ev = MakeEventSettings();
        var rota = new Rota { Id = Guid.NewGuid(), Name = "Setup crew" };
        var signup = MakeSignup(userId, signupBlockId: null,
            MakeShift(rota, dayOffset: 0, isAllDay: false, startTime: new LocalTime(9, 0), durationHours: 4),
            SignupStatus.Pending);

        var provider = MakeProvider(userId, ev, [signup]);

        var snapshot = await provider.LoadAsync(userId, CancellationToken.None);

        snapshot.UpcomingShifts.Should().HaveCount(1);
        var entry = snapshot.UpcomingShifts[0];
        entry.Key.Should().Be(signup.Id);
        entry.DayCount.Should().Be(1);
        entry.StartDate.Should().Be(entry.EndDate);
    }

    [HumansFact]
    public async Task LoadAsync_filters_out_past_shifts()
    {
        var userId = Guid.NewGuid();
        var ev = MakeEventSettings();
        var rota = new Rota { Id = Guid.NewGuid(), Name = "Old" };
        var pastSignup = MakeSignup(userId, signupBlockId: null,
            MakeShift(rota, dayOffset: -100, isAllDay: true), SignupStatus.Confirmed);

        var provider = MakeProvider(userId, ev, [pastSignup]);

        var snapshot = await provider.LoadAsync(userId, CancellationToken.None);

        snapshot.UpcomingShifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LoadAsync_excludes_inactive_signup_states()
    {
        // Pending and Confirmed are surfaced; Refused, Bailed, Cancelled are not.
        var userId = Guid.NewGuid();
        var ev = MakeEventSettings();
        var rota = new Rota { Id = Guid.NewGuid(), Name = "R" };
        var pending = MakeSignup(userId, null, MakeShift(rota, dayOffset: 1, isAllDay: true), SignupStatus.Pending);
        var confirmed = MakeSignup(userId, null, MakeShift(rota, dayOffset: 2, isAllDay: true), SignupStatus.Confirmed);
        var refused = MakeSignup(userId, null, MakeShift(rota, dayOffset: 3, isAllDay: true), SignupStatus.Refused);
        var bailed = MakeSignup(userId, null, MakeShift(rota, dayOffset: 4, isAllDay: true), SignupStatus.Bailed);
        var cancelled = MakeSignup(userId, null, MakeShift(rota, dayOffset: 5, isAllDay: true), SignupStatus.Cancelled);

        var provider = MakeProvider(userId, ev, [pending, confirmed, refused, bailed, cancelled]);
        var snapshot = await provider.LoadAsync(userId, CancellationToken.None);

        snapshot.UpcomingShifts.Should().HaveCount(2);
        snapshot.UpcomingShifts.Select(e => e.Status).Should().BeEquivalentTo([SignupStatus.Pending, SignupStatus.Confirmed
        ]);
    }

    [HumansFact]
    public async Task LoadAsync_returns_empty_when_no_active_event()
    {
        var userId = Guid.NewGuid();
        var provider = MakeProvider(userId, activeEvent: null, signups: []);
        var snapshot = await provider.LoadAsync(userId, CancellationToken.None);
        snapshot.UpcomingShifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task LoadAsync_populates_OpenTicketIds_from_ticket_service()
    {
        var userId = Guid.NewGuid();
        var ev = MakeEventSettings();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var provider = MakeProvider(userId, ev, [], openTicketIds: ids);

        var snapshot = await provider.LoadAsync(userId, CancellationToken.None);

        snapshot.OpenTicketIds.Should().BeEquivalentTo(ids);
    }

    [HumansFact]
    public async Task LoadAsync_populates_team_memberships_with_role()
    {
        var userId = Guid.NewGuid();
        var ev = MakeEventSettings();
        var memberships = new[]
        {
            new TeamMembership("Build", TeamMemberRole.Coordinator),
            new TeamMembership("Cantina", TeamMemberRole.Member)
        };
        var provider = MakeProvider(userId, ev, [], teamMemberships: memberships);

        var snapshot = await provider.LoadAsync(userId, CancellationToken.None);

        snapshot.Teams.Should().BeEquivalentTo(memberships);
    }

    private static AgentUserSnapshotProvider MakeProvider(
        Guid userId,
        EventSettings? activeEvent,
        IReadOnlyList<ShiftSignup> signups,
        IReadOnlyList<Guid>? openTicketIds = null,
        IReadOnlyList<TeamMembership>? teamMemberships = null)
    {
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(
                new User { Id = userId, DisplayName = "T", PreferredLanguage = "es" }
                    .ToUserInfo()));
        var roles = Substitute.For<IRoleAssignmentService>();
        roles.GetActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var teams = Substitute.For<ITeamServiceRead>();
        // Build a TeamInfo dict that mirrors the desired memberships so the
        // reshape idiom (GetTeamsAsync → filter by member) returns the same data.
        var membershipsToReturn = teamMemberships ?? [];
        var teamInfos = membershipsToReturn
            .Select((m, i) => new TeamInfo(
                Id: Guid.NewGuid(),
                Name: m.TeamName,
                Description: null,
                Slug: $"team-{i}",
                IsActive: true,
                IsSystemTeam: false,
                SystemTeamType: SystemTeamType.None,
                RequiresApproval: false,
                IsPublicPage: false,
                IsHidden: m.IsHidden,
                IsPromotedToDirectory: false,
                CreatedAt: NodaTime.Instant.MinValue,
                Members: [new TeamMemberInfo(Guid.NewGuid(), userId, "T", null, null, m.Role, NodaTime.Instant.MinValue)]))
            .ToList();
        var teamDict = teamInfos.ToDictionary(t => t.Id);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(teamDict));

        var consents = Substitute.For<IConsentServiceRead>();
        consents.GetPendingDocumentNamesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var feedback = Substitute.For<IFeedbackService>();
        feedback.GetOpenFeedbackIdsForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tickets = Substitute.For<ITicketServiceRead>();
        tickets.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, [])
            {
                OpenTicketOrderIds = openTicketIds ?? [],
            });

        var shiftView = Substitute.For<IShiftView>();
        // Pre-built view: the inner ShiftViewService filters Signups to the
        // active event, so tests with no active event still pass an empty view.
        var view = new ShiftUserView(
            UserId: userId,
            Profile: null,
            Availability: null,
            BuildStatus: null,
            TagPreferences: [],
            Signups: activeEvent is null ? [] : signups);
        shiftView.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserView>(view));

        var shiftMgmt = Substitute.For<IShiftManagementService>();
        shiftMgmt.GetActiveAsync().Returns(activeEvent);

        var clock = new FakeClock(Instant.FromUtc(2026, 6, 1, 0, 0));

        return new AgentUserSnapshotProvider(
            users, roles, teams, consents, feedback, tickets,
            shiftView, shiftMgmt, clock);
    }

    private static EventSettings MakeEventSettings() => new()
    {
        Id = Guid.NewGuid(),
        EventName = "Nowhere 2026",
        Year = 2026,
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new LocalDate(2026, 7, 1),
        BuildStartOffset = -14,
        EventEndOffset = 6,
        StrikeEndOffset = 9,
        IsActive = true
    };

    private static Shift MakeShift(
        Rota rota, int dayOffset, bool isAllDay,
        LocalTime? startTime = null, double durationHours = 0) => new()
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            Rota = rota,
            DayOffset = dayOffset,
            IsAllDay = isAllDay,
            StartTime = startTime ?? new LocalTime(8, 0),
            Duration = Duration.FromHours(durationHours),
            MinVolunteers = 1,
            MaxVolunteers = 5
        };

    private static ShiftSignup MakeSignup(
        Guid userId, Guid? signupBlockId, Shift shift, SignupStatus status) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shift.Id,
            Shift = shift,
            SignupBlockId = signupBlockId,
            Status = status
        };
}
