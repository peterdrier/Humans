using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class TicketsContactsAdminControllerTests
{
    private static UserManager<User> NoOpUserManager()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        return Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    [HumansFact]
    public async Task Index_RendersPreview_WithPlanAndProjectedRows()
    {
        var import = Substitute.For<IAttendeeContactImportService>();
        var attendeeId = Guid.NewGuid();
        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(attendeeId, "a@x.com", "A", "tkt_a",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null),
            }, 1);
        import.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);

        var controller = new TicketsContactsAdminController(
            import, NoOpUserManager(),
            NullLogger<TicketsContactsAdminController>.Instance);

        var result = await controller.Index(default);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var vm = view.Model.Should().BeOfType<ContactImportPreviewViewModel>().Subject;
        vm.Plan.Should().BeSameAs(plan);
        vm.Rows.Should().ContainSingle()
            .Which.AttendeeId.Should().Be(attendeeId);
    }
}
