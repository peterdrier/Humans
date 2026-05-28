using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

public class OnsiteRosterServiceTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();

    private OnsiteRosterService NewService() =>
        new(_users, _shifts, _camps, _teams, _roles);

    [HumansFact]
    public async Task GetRosterAsync_YearZero_ReturnsEmpty()
    {
        var service = NewService();
        var result = await service.GetRosterAsync(0, null, null, null, default);
        result.Rows.Should().BeEmpty();
        await _users.DidNotReceive().GetOnsiteUsersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRosterAsync_EmptyOnsite_ReturnsEmpty()
    {
        _users.GetOnsiteUsersAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<OnsiteUserRow>());

        var service = NewService();
        var result = await service.GetRosterAsync(2026, null, null, null, default);

        result.Rows.Should().BeEmpty();
        await _camps.DidNotReceive().GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRosterAsync_NoFilters_ReturnsAllOnsite_WithJoinedNames()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var ts = Instant.FromUtc(2026, 7, 8, 12, 0);

        _users.GetOnsiteUsersAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<OnsiteUserRow>
            {
                new(aliceId, "Alice", ts),
                new(bobId, "Bob", ts),
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
        var result = await service.GetRosterAsync(2026, null, null, null, default);

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

        _users.GetOnsiteUsersAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<OnsiteUserRow>
            {
                new(aliceId, "Alice", ts),
                new(bobId, "Bob", ts),
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
        var result = await service.GetRosterAsync(2026, null, null, "Board", default);

        result.Rows.Should().ContainSingle();
        result.Rows[0].UserId.Should().Be(aliceId);
    }

    [HumansFact]
    public async Task GetRosterAsync_SkipsRowsWithNullCheckedInAt()
    {
        // OnsiteUserRow.CheckedInAt is null when the snapshot view of the cache
        // happens to have an Attended row without a stored timestamp (legacy /
        // pre-#736 rows). The service filters these out — they don't belong in
        // a "currently onsite" view.
        var nullId = Guid.NewGuid();
        var liveId = Guid.NewGuid();
        var ts = Instant.FromUtc(2026, 7, 8, 12, 0);

        _users.GetOnsiteUsersAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<OnsiteUserRow>
            {
                new(nullId, "NoTimestamp", null),
                new(liveId, "Live", ts),
            });
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _roles.GetActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot>());

        var service = NewService();
        var result = await service.GetRosterAsync(2026, null, null, null, default);

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

        _users.GetOnsiteUsersAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<OnsiteUserRow> { new(aliceId, "Alice", ts) });

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                new(campId, "thunderdome", "x@y", "p", false, 0,
                    new List<CampSeasonInfo>
                    {
                        new(seasonId, campId, "thunderdome", 2026, null, "Thunderdome 2026",
                            "", "", new List<Humans.Domain.Enums.CampVibe>(),
                            Humans.Domain.Enums.CampSeasonStatus.Active,
                            Humans.Domain.Enums.YesNoMaybe.Yes, Humans.Domain.Enums.YesNoMaybe.No,
                            Humans.Domain.Enums.AdultPlayspacePolicy.No,
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
        var result = await service.GetRosterAsync(2026, null, null, null, default);

        result.Rows.Should().ContainSingle();
        result.Rows[0].CampNames.Should().Equal("Thunderdome 2026");
        result.AvailableCamps.Should().Equal("Thunderdome 2026");
    }
}
