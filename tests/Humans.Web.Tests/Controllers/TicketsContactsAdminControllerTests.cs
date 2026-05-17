using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Domain.Entities;
using Humans.Web.Constants;
using Humans.Web.Controllers;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class TicketsContactsAdminControllerTests
{
    private static (TicketsContactsAdminController Ctrl, User CurrentUser, IUserService Users)
        NewController(IAttendeeContactImportService import)
    {
        var users = Substitute.For<IUserService>();
        var currentUser = new User { Id = Guid.NewGuid() };
        users.GetUserInfoAsync(currentUser.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                currentUser,
                [],
                [],
                [],
                profile: null,
                [],
                [],
                [],
                [])));

        var ctrl = new TicketsContactsAdminController(
            import, users,
            NullLogger<TicketsContactsAdminController>.Instance);

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                    new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, currentUser.Id.ToString())
                ],
                "test")),
        };

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return (ctrl, currentUser, users);
    }

    [HumansFact]
    public async Task Index_RendersPreview_WithPlanAndProjectedRows()
    {
        var import = Substitute.For<IAttendeeContactImportService>();
        var attendeeId = Guid.NewGuid();
        var plan = new AttendeeImportPlan([
            new AttendeeImportDecision(attendeeId, "a@x.com", "A", "tkt_a",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null)
        ], 1);
        import.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);

        var (controller, _, _) = NewController(import);

        var result = await controller.Index(CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var vm = view.Model.Should().BeOfType<ContactImportPreviewViewModel>().Subject;
        vm.Plan.Should().BeSameAs(plan);
        vm.Rows.Should().ContainSingle()
            .Which.AttendeeId.Should().Be(attendeeId);
    }

    [HumansFact]
    public async Task Apply_PassesSelectedIdsAndActorToService_AndRedirectsWithInfoMessage()
    {
        var import = Substitute.For<IAttendeeContactImportService>();
        var plan = new AttendeeImportPlan([], 0);
        import.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);
        import.ApplyAsync(Arg.Any<AttendeeImportPlan>(),
                Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AttendeeImportResult(
                TotalAttempted: 1, UsersCreated: 1, AttachedToExistingVerified: 0,
                UnverifiedRowsDeletedAndUserCreated: 0, AmbiguousSkipped: 0, NoEmailSkipped: 0,
                VanishedBetweenPlanAndApply: 0, Errors: 0,
                Elapsed: Duration.FromSeconds(1)));

        var (controller, currentUser, _) = NewController(import);

        var selectedId = Guid.NewGuid();
        var result = await controller.Apply([selectedId], CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(TicketsContactsAdminController.Index));
        controller.TempData[TempDataKeys.InfoMessage].Should().NotBeNull();

        await import.Received(1).ApplyAsync(
            plan,
            Arg.Is<IReadOnlySet<Guid>>(s => s.Contains(selectedId) && s.Count == 1),
            currentUser.Id,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_EmptySelection_RedirectsWithErrorMessage_NoServiceCall()
    {
        var import = Substitute.For<IAttendeeContactImportService>();
        var (controller, _, _) = NewController(import);

        var result = await controller.Apply([], CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        controller.TempData[TempDataKeys.ErrorMessage].Should().NotBeNull();
        await import.DidNotReceive().ApplyAsync(
            Arg.Any<AttendeeImportPlan>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
