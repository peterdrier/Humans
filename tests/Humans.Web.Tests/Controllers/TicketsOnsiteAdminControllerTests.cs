using AwesomeAssertions;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
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
        IOnsiteRosterService roster)
    {
        var ctrl = new TicketsOnsiteAdminController(users, shifts, roster);

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
    public async Task Index_NoActiveEvent_DispatchesYearZero_AndReturnsEmpty()
    {
        var users = Substitute.For<IUserService>();
        var shifts = Substitute.For<IShiftManagementService>();
        shifts.GetActiveAsync().Returns((EventSettings?)null);

        var roster = Substitute.For<IOnsiteRosterService>();
        roster.GetRosterAsync(0, null, null, null, Arg.Any<CancellationToken>())
            .Returns(new OnsiteRosterResult([], [], [], []));

        var ctrl = NewController(users, shifts, roster);

        var result = await ctrl.Index(camp: null, team: null, role: null, ct: default);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<OnsiteRosterViewModel>().Subject;
        vm.Year.Should().Be(0);
        vm.Rows.Should().BeEmpty();

        await roster.Received(1).GetRosterAsync(0, null, null, null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_SortsRowsByCheckedInAt_Descending()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var earlier = Instant.FromUtc(2026, 7, 8, 10, 0);
        var later = Instant.FromUtc(2026, 7, 8, 18, 0);

        var users = Substitute.For<IUserService>();
        var shifts = Substitute.For<IShiftManagementService>();
        shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026 });

        var roster = Substitute.For<IOnsiteRosterService>();
        roster.GetRosterAsync(2026, null, null, null, Arg.Any<CancellationToken>())
            .Returns(new OnsiteRosterResult(
                Rows: new List<OnsiteRosterRow>
                {
                    new(aliceId, earlier, [], [], []),
                    new(bobId, later, [], [], []),
                },
                AvailableCamps: [],
                AvailableTeams: [],
                AvailableRoles: []));

        var ctrl = NewController(users, shifts, roster);

        var result = await ctrl.Index(camp: null, team: null, role: null, ct: default);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<OnsiteRosterViewModel>().Subject;
        vm.Rows.Should().HaveCount(2);
        vm.Rows[0].UserId.Should().Be(bobId);
        vm.Rows[1].UserId.Should().Be(aliceId);
    }

    [HumansFact]
    public async Task Index_ForwardsFilterParamsToService()
    {
        var users = Substitute.For<IUserService>();
        var shifts = Substitute.For<IShiftManagementService>();
        shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026 });

        var roster = Substitute.For<IOnsiteRosterService>();
        roster.GetRosterAsync(2026, "Cosmic Camp", "Gate", "Board", Arg.Any<CancellationToken>())
            .Returns(new OnsiteRosterResult([], [], [], []));

        var ctrl = NewController(users, shifts, roster);

        await ctrl.Index(camp: "Cosmic Camp", team: "Gate", role: "Board", ct: default);

        await roster.Received(1).GetRosterAsync(
            2026, "Cosmic Camp", "Gate", "Board", Arg.Any<CancellationToken>());
    }
}
