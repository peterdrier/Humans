using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Controllers;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class TicketsOnsiteAdminControllerTests
{
    private static TicketsOnsiteAdminController NewController(
        IUserService users,
        IShiftManagementService shifts,
        ICampService camps,
        ITeamService teams,
        IRoleAssignmentService roles)
    {
        var ctrl = new TicketsOnsiteAdminController(users, shifts, camps, teams, roles);

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Index" },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    [HumansFact]
    public async Task Index_NoActiveEvent_ReturnsEmptyRoster()
    {
        var users = Substitute.For<IUserService>();
        var shifts = Substitute.For<IShiftManagementService>();
        shifts.GetActiveAsync().Returns((EventSettings?)null);

        var ctrl = NewController(users, shifts,
            Substitute.For<ICampService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<IRoleAssignmentService>());

        var result = await ctrl.Index(camp: null, team: null, role: null, ct: default);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<OnsiteRosterViewModel>().Subject;
        vm.Year.Should().Be(0);
        vm.Rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Index_ReturnsOnsiteUsers_SortedMostRecentFirst()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var earlier = Instant.FromUtc(2026, 7, 8, 10, 0);
        var later = Instant.FromUtc(2026, 7, 8, 18, 0);

        var users = Substitute.For<IUserService>();
        users.GetOnsiteUsersAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<OnsiteUserRow>
            {
                new(aliceId, "Alice", earlier),
                new(bobId, "Bob", later),
            });

        var shifts = Substitute.For<IShiftManagementService>();
        shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026 });

        var camps = Substitute.For<ICampService>();
        camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());

        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        var roles = Substitute.For<IRoleAssignmentService>();
        roles.GetActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot>());

        var ctrl = NewController(users, shifts, camps, teams, roles);

        var result = await ctrl.Index(camp: null, team: null, role: null, ct: default);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<OnsiteRosterViewModel>().Subject;
        vm.Year.Should().Be(2026);
        vm.Rows.Should().HaveCount(2);
        // Sorted by CheckedInAt descending.
        vm.Rows[0].UserId.Should().Be(bobId);
        vm.Rows[1].UserId.Should().Be(aliceId);
    }

    [HumansFact]
    public async Task Index_RoleFilter_NarrowsToRoleHolders()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var ts = Instant.FromUtc(2026, 7, 8, 12, 0);

        var users = Substitute.For<IUserService>();
        users.GetOnsiteUsersAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<OnsiteUserRow>
            {
                new(aliceId, "Alice", ts),
                new(bobId, "Bob", ts),
            });

        var shifts = Substitute.For<IShiftManagementService>();
        shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026 });

        var camps = Substitute.For<ICampService>();
        camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>());

        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        var roles = Substitute.For<IRoleAssignmentService>();
        roles.GetActiveForUserAsync(aliceId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot> { new("Board", ValidTo: null) });
        roles.GetActiveForUserAsync(bobId, Arg.Any<CancellationToken>())
            .Returns(new List<RoleAssignmentSnapshot>()); // none

        var ctrl = NewController(users, shifts, camps, teams, roles);

        var result = await ctrl.Index(camp: null, team: null, role: "Board", ct: default);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<OnsiteRosterViewModel>().Subject;
        vm.Rows.Should().ContainSingle();
        vm.Rows[0].UserId.Should().Be(aliceId);
        vm.Rows[0].RoleNames.Should().Contain("Board");
        vm.AvailableRoles.Should().Contain("Board");
    }
}
