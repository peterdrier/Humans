using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Barrio coordinator Withdraw POST. Approved → audited moderation action;
/// Pending → unaudited self-withdraw. Non-managers of the camp are guarded by
/// <c>ResolveCampManagementAsync</c>.
/// </summary>
public class BarrioEventsControllerWithdrawTests
{
    private readonly IEventService _guide = Substitute.For<IEventService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICampService _campService = Substitute.For<ICampService>();
    private readonly IAuthorizationService _authz = Substitute.For<IAuthorizationService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ILogger<BarrioEventsController> _logger = NullLogger<BarrioEventsController>.Instance;

    [HumansFact]
    public async Task Withdraw_ApprovedEvent_UsesAuditedServicePath()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var camp = MakeCamp();
        var ctrl = BuildSut(userId, camp, canManage: true);

        _guide.GetCampEventAsync(eventId, camp.Id).Returns(new Event
        {
            Id = eventId,
            Title = "Workshop",
            CampId = camp.Id,
            Status = EventStatus.Approved
        });

        var result = await ctrl.Withdraw(camp.Slug, eventId);

        await _guide.Received(1).WithdrawApprovedEventAsync(eventId, userId, null);
        await _guide.DidNotReceiveWithAnyArgs().WithdrawEventAsync(Arg.Any<Event>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    [HumansFact]
    public async Task Withdraw_PendingEvent_UsesUnauditedSelfWithdraw()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var camp = MakeCamp();
        var ctrl = BuildSut(userId, camp, canManage: true);

        var guideEvent = new Event
        {
            Id = eventId,
            Title = "Workshop",
            CampId = camp.Id,
            Status = EventStatus.Pending
        };
        _guide.GetCampEventAsync(eventId, camp.Id).Returns(guideEvent);

        var result = await ctrl.Withdraw(camp.Slug, eventId);

        await _guide.Received(1).WithdrawEventAsync(guideEvent);
        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    [HumansFact]
    public async Task Withdraw_NotCampManager_Forbids()
    {
        var userId = Guid.NewGuid();
        var camp = MakeCamp();
        var ctrl = BuildSut(userId, camp, canManage: false);

        var result = await ctrl.Withdraw(camp.Slug, Guid.NewGuid());

        Assert.IsType<ForbidResult>(result);
        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        await _guide.DidNotReceiveWithAnyArgs().WithdrawEventAsync(Arg.Any<Event>());
    }

    [HumansFact]
    public async Task Withdraw_WrongStatus_DoesNotCallService()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var camp = MakeCamp();
        var ctrl = BuildSut(userId, camp, canManage: true);

        _guide.GetCampEventAsync(eventId, camp.Id).Returns(new Event
        {
            Id = eventId,
            CampId = camp.Id,
            Status = EventStatus.Rejected
        });

        var result = await ctrl.Withdraw(camp.Slug, eventId);

        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        await _guide.DidNotReceiveWithAnyArgs().WithdrawEventAsync(Arg.Any<Event>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    private BarrioEventsController BuildSut(Guid userId, CampLookup camp, bool canManage)
    {
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId));
        _campService.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(camp);
        _authz.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(canManage ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        var ctrl = new BarrioEventsController(
            _userService, _campService, _authz, _guide, _users, _clock, _emailService, _logger);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        http.RequestServices = services.BuildServiceProvider();
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Withdraw" },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static CampLookup MakeCamp() => new(
        Id: Guid.NewGuid(),
        Slug: "test-camp",
        ContactEmail: "camp@example.test",
        Seasons: [],
        Leads: []);

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "Coordinator",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
}
