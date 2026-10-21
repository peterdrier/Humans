using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Services;
using Humans.Base.Enums;
using NodaTime;
using NSubstitute;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Tests.Services;

public class OnsiteRosterServiceTests
{
    private readonly IEventParticipationManagement _users = Substitute.For<IEventParticipationManagement>();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();

    private OnsiteRosterService NewService() =>
        new(_users, _camps, _teams, _roles);

    private static EventParticipationInfo Onsite(Guid userId, Instant? checkedInAt) =>
        new(userId, 2026, ParticipationStatus.Attended, ParticipationSource.TicketSync, null, checkedInAt);

    [HumansFact]
    public async Task GetRosterAsync_YearZero_ReturnsEmpty()
    {
        var service = NewService();
        var result = await service.GetRosterAsync(0, null, null, null, Xunit.TestContext.Current.CancellationToken);
        result.Rows.Should().BeEmpty();
        await _users.DidNotReceive().GetAllParticipationsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRosterAsync_EmptyOnsite_ReturnsEmpty()
    {
        _users.GetAllParticipationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EventParticipationInfo>());

        var service = NewService();
        var result = await service.GetRosterAsync(2026, null, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Rows.Should().BeEmpty();
        await _camps.DidNotReceive().GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRosterAsync_NoFilters_ReturnsAllOnsite_WithJoinedNames()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var ts = Instant.FromUtc(2026, 7, 8, 12, 0);

        _users.GetAllParticipationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EventParticipationInfo>
            {
                Onsite(aliceId, ts),
                Onsite(bobId, ts),
            });

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _roles.GetActiveForUserAsync(aliceId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot> { new("Board", ValidTo: null) });
        _roles.GetActiveForUserAsync(bobId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot>());

        var service = NewService();
        var result = await service.GetRosterAsync(2026, null, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Rows.Should().HaveCount(2);
        result.Rows.Single(r => r.UserId == aliceId).RoleNames.Should().Contain("Board");
        result.AvailableRoles.Should().Contain("Board");
    }

    [HumansFact]
    public async Task GetRosterAsync_RoleFilter_NarrowsToHolders()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var ts = Instant.FromUtc(2026, 7, 8, 12, 0);

        _users.GetAllParticipationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EventParticipationInfo>
            {
                Onsite(aliceId, ts),
                Onsite(bobId, ts),
            });

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _roles.GetActiveForUserAsync(aliceId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot> { new("Board", ValidTo: null) });
        _roles.GetActiveForUserAsync(bobId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot>());

        var service = NewService();
        var result = await service.GetRosterAsync(2026, null, null, "Board", Xunit.TestContext.Current.CancellationToken);

        result.Rows.Should().ContainSingle();
        result.Rows[0].UserId.Should().Be(aliceId);
    }

    [HumansFact]
    public async Task GetRosterAsync_SkipsRowsWithNullCheckedInAt()
    {
        // An Attended row can lack a stored check-in timestamp. The service filters
        // these out — they don't belong in a "currently onsite" view.
        var nullId = Guid.NewGuid();
        var liveId = Guid.NewGuid();
        var ts = Instant.FromUtc(2026, 7, 8, 12, 0);

        _users.GetAllParticipationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EventParticipationInfo>
            {
                Onsite(nullId, null),
                Onsite(liveId, ts),
            });
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _roles.GetActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot>());

        var service = NewService();
        var result = await service.GetRosterAsync(2026, null, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Rows.Should().ContainSingle();
        result.Rows[0].UserId.Should().Be(liveId);
    }

    [HumansFact]
    public async Task GetRosterAsync_JoinsCampNames_FromProjectedCampMembers()
    {
        var aliceId = Guid.NewGuid();
        var ts = Instant.FromUtc(2026, 7, 8, 12, 0);
        var campId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();

        _users.GetAllParticipationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EventParticipationInfo> { Onsite(aliceId, ts) });

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                new(campId, "thunderdome", "x@y", "p", false, 0,
                    new List<CampSeasonInfo>
                    {
                        new(seasonId, campId, "thunderdome", 2026, null, "Thunderdome 2026",
                            "", "", new List<CampVibe>(),
                            CampSeasonStatus.Active,
                            YesNoMaybe.Yes, YesNoMaybe.No,
                            AdultPlayspacePolicy.No,
                            1, null, null, null, 0, null, null)
                        {
                            Members =
                            [
                                new(Guid.NewGuid(), aliceId, CampMemberStatus.Active, ts, ts, false),
                            ],
                        },
                    }),
            });

        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _roles.GetActiveForUserAsync(aliceId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot>());

        var service = NewService();
        var result = await service.GetRosterAsync(2026, null, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Rows.Should().ContainSingle();
        result.Rows[0].CampNames.Should().Equal("Thunderdome 2026");
        result.AvailableCamps.Should().Equal("Thunderdome 2026");
    }
}
