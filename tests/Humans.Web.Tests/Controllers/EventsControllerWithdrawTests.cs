using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
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
/// Submitter-initiated Withdraw on the individual-event flow. Approved → audited
/// path; Pending/Draft → unaudited self-withdraw. Non-owner is guarded by the
/// service layer (<see cref="IEventService.GetUserEventAsync"/> scopes by user id).
/// </summary>
public class EventsControllerWithdrawTests
{
    private readonly IEventService _guide = Substitute.For<IEventService>();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ILogger<EventsController> _logger = NullLogger<EventsController>.Instance;

    [HumansFact]
    public async Task Withdraw_ApprovedEvent_UsesAuditedServicePath()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        _guide.GetUserEventAsync(eventId, userId).Returns(new Event
        {
            Id = eventId,
            Title = "Yoga",
            Status = EventStatus.Approved
        });

        var result = await ctrl.Withdraw(eventId);

        await _guide.Received(1).WithdrawApprovedEventAsync(eventId, userId, null);
        await _guide.DidNotReceiveWithAnyArgs().WithdrawEventAsync(Arg.Any<Event>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    [HumansTheory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Pending)]
    public async Task Withdraw_PreApprovalStatuses_UseUnauditedSelfWithdraw(EventStatus status)
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        var guideEvent = new Event { Id = eventId, Title = "Setup", Status = status };
        _guide.GetUserEventAsync(eventId, userId).Returns(guideEvent);

        var result = await ctrl.Withdraw(eventId);

        await _guide.Received(1).WithdrawEventAsync(guideEvent);
        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    [HumansFact]
    public async Task Withdraw_NotOwnerOrUnknown_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        // Service-layer ownership filter returns null for non-owners or unknown ids.
        _guide.GetUserEventAsync(eventId, userId).Returns((Event?)null);

        var result = await ctrl.Withdraw(eventId);

        Assert.IsType<NotFoundResult>(result);
        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        await _guide.DidNotReceiveWithAnyArgs().WithdrawEventAsync(Arg.Any<Event>());
    }

    [HumansFact]
    public async Task Withdraw_WrongStatus_DoesNotCallService()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        _guide.GetUserEventAsync(eventId, userId).Returns(new Event
        {
            Id = eventId,
            Title = "Old",
            Status = EventStatus.Rejected
        });

        var result = await ctrl.Withdraw(eventId);

        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        await _guide.DidNotReceiveWithAnyArgs().WithdrawEventAsync(Arg.Any<Event>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    private EventsController BuildSut(Guid userId)
    {
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId));

        var ctrl = new EventsController(
            _guide, _camps, _users, _userService, _clock, _emailService, _logger);

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

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "Submitter",
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
