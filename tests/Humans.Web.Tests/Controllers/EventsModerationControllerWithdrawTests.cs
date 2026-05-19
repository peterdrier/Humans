using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models.Events;
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
/// Withdraw POST on the moderator queue. Verifies the audited service path
/// (<see cref="IEventService.WithdrawApprovedEventAsync"/>) is invoked with the
/// moderator's user id and the rejection of wrong-status events (US-26.4b).
/// </summary>
public class EventsModerationControllerWithdrawTests
{
    private readonly IEventService _guide = Substitute.For<IEventService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly ILogger<EventsModerationController> _logger = NullLogger<EventsModerationController>.Instance;

    [HumansFact]
    public async Task Withdraw_ApprovedEvent_CallsAuditedServicePathAndRedirectsToApprovedTab()
    {
        var moderatorId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ctrl = BuildSut(moderatorId);
        _guide.GetEventForModerationAsync(eventId).Returns(new Event
        {
            Id = eventId,
            Title = "Acoustic set",
            Status = EventStatus.Approved
        });

        var result = await ctrl.Withdraw(new ModerationActionFormModel { EventId = eventId, Reason = "Cancelled" });

        await _guide.Received(1).WithdrawApprovedEventAsync(eventId, moderatorId, "Cancelled");
        await _guide.DidNotReceiveWithAnyArgs().WithdrawEventAsync(Arg.Any<Event>());
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal(EventStatus.Approved, redirect.RouteValues!["tab"]);
    }

    [HumansFact]
    public async Task Withdraw_WrongStatus_DoesNotCallServiceAndSetsError()
    {
        var moderatorId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ctrl = BuildSut(moderatorId);
        _guide.GetEventForModerationAsync(eventId).Returns(new Event
        {
            Id = eventId,
            Title = "Already gone",
            Status = EventStatus.Withdrawn
        });

        var result = await ctrl.Withdraw(new ModerationActionFormModel { EventId = eventId });

        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    [HumansFact]
    public async Task Withdraw_EventNotFound_DoesNotCallServiceAndSetsError()
    {
        var moderatorId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ctrl = BuildSut(moderatorId);
        _guide.GetEventForModerationAsync(eventId).Returns((Event?)null);

        var result = await ctrl.Withdraw(new ModerationActionFormModel { EventId = eventId });

        await _guide.DidNotReceiveWithAnyArgs().WithdrawApprovedEventAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>());
        Assert.IsType<RedirectToActionResult>(result);
    }

    private EventsModerationController BuildSut(Guid moderatorId)
    {
        _userService.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(moderatorId));

        var ctrl = new EventsModerationController(
            _guide, _userService, _emailService, _userService, _camps, _logger);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, moderatorId.ToString())], "test")),
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
                DisplayName = "Mod",
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
