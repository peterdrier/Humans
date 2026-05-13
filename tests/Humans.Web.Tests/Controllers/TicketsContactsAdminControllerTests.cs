using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
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

    private static TicketsContactsAdminController NewController(IAttendeeContactImportService import)
    {
        var ctrl = new TicketsContactsAdminController(
            import, NoOpUserManager(),
            NullLogger<TicketsContactsAdminController>.Instance);
        var http = new DefaultHttpContext();
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return ctrl;
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

        var controller = NewController(import);

        var result = await controller.Index(default);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var vm = view.Model.Should().BeOfType<ContactImportPreviewViewModel>().Subject;
        vm.Plan.Should().BeSameAs(plan);
        vm.Rows.Should().ContainSingle()
            .Which.AttendeeId.Should().Be(attendeeId);
    }

    [HumansFact]
    public async Task Apply_PassesSelectedIdsToService_AndRedirectsWithBanner()
    {
        var import = Substitute.For<IAttendeeContactImportService>();
        var plan = new AttendeeImportPlan(Array.Empty<AttendeeImportDecision>(), 0);
        import.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);
        import.ApplyAsync(Arg.Any<AttendeeImportPlan>(),
                Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new AttendeeImportResult(
                TotalAttempted: 1, UsersCreated: 1, AttachedToExistingVerified: 0,
                UnverifiedRowsDeletedAndUserCreated: 0, AmbiguousSkipped: 0, NoEmailSkipped: 0,
                VanishedBetweenPlanAndApply: 0, Errors: 0,
                Elapsed: Duration.FromSeconds(1)));

        var controller = NewController(import);

        var selectedId = Guid.NewGuid();
        var result = await controller.Apply(new[] { selectedId }, default);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(TicketsContactsAdminController.Index));
        controller.TempData["Banner"].Should().NotBeNull();

        await import.Received(1).ApplyAsync(
            plan,
            Arg.Is<IReadOnlySet<Guid>>(s => s.Contains(selectedId) && s.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_EmptySelection_RedirectsWithValidationBanner_NoServiceCall()
    {
        var import = Substitute.For<IAttendeeContactImportService>();
        var controller = NewController(import);

        var result = await controller.Apply(Array.Empty<Guid>(), default);

        result.Should().BeOfType<RedirectToActionResult>();
        controller.TempData["Banner"].Should().NotBeNull();
        await import.DidNotReceive().ApplyAsync(
            Arg.Any<AttendeeImportPlan>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>());
    }
}
