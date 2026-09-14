using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Controllers;
using Humans.Events.Domain;
using Humans.Events.Models;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Events.Tests.Controllers;

public sealed class EventsModerationControllerTests
{
    private readonly IEventService _guide = Substitute.For<IEventService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();

    [HumansFact]
    public async Task Approve_pending_event_applies_the_approval_with_submitter_edit_url()
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            GuideSharedVenueId = Guid.NewGuid(),
            Title = "Pending event",
            Description = "Description",
            StartAt = Instant.FromUtc(2026, 8, 1, 18, 0),
            DurationMinutes = 60,
            Status = EventStatus.Pending,
        };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));

        var controller = BuildController(moderatorId, guideEvent.Id);

        var result = await controller.Approve(new ModerationActionFormModel { EventId = guideEvent.Id });

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Index");
        await _guide.Received(1).ApplyModerationAsync(
            guideEvent.Id, moderatorId, EventModerationActionType.Approved, null,
            $"/Events/Submit/{guideEvent.Id}/Edit", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_carries_the_actor_of_each_history_entry_newest_first()
    {
        var moderatorId = Guid.NewGuid();
        var laterModeratorId = Guid.NewGuid();
        var submitterId = Guid.NewGuid();
        var pending = new EventInfo(
            Guid.NewGuid(), null, null, submitterId, Guid.NewGuid(), "Music", "music", false, null,
            "Pending event", "Description", null, null, Instant.FromUtc(2026, 8, 1, 18, 0), 60,
            false, null, null, EventStatus.Pending, Instant.FromUtc(2026, 7, 1, 0, 0), Instant.FromUtc(2026, 7, 1, 0, 0),
            [
                new EventModerationHistoryInfo(moderatorId, EventModerationActionType.ResubmitRequested, "Fix the time", Instant.FromUtc(2026, 7, 2, 0, 0)),
                new EventModerationHistoryInfo(laterModeratorId, EventModerationActionType.Approved, null, Instant.FromUtc(2026, 7, 3, 0, 0)),
            ]);
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventGuideSettingsView?)null);
        _guide.GetEventStatusCountsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<EventStatus, int>());
        _guide.GetEventsByStatusAsync(EventStatus.Pending, Arg.Any<CancellationToken>()).Returns([pending]);
        _users.GetUserInfoAsync(submitterId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(submitterId)));

        var controller = BuildController(moderatorId, pending.Id);

        var result = await controller.Index(null);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ModerationQueueViewModel>().Which;
        var history = model.Events.Single().History;
        history.Select(h => h.ActorUserId).Should().Equal(laterModeratorId, moderatorId);
    }

    private EventsModerationController BuildController(Guid moderatorId, Guid eventId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, moderatorId.ToString())], "test")),
        };
        var controller = new EventsModerationController(
            _guide, _users, Substitute.For<ICampServiceRead>(),
            NullLogger<EventsModerationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
        controller.Url = Substitute.For<IUrlHelper>();
        controller.Url.Action(Arg.Any<UrlActionContext>()).Returns($"/Events/Submit/{eventId}/Edit");
        return controller;
    }

    private static UserInfo UserInfoFor(Guid userId) => UserInfo.Create(
        new User { Id = userId, DisplayName = "Moderator", PreferredLanguage = "en", CreatedAt = Instant.MinValue },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: UserFixtures.Profile(burnerName: "Moderator", firstName: "Mod", lastName: "Erator"),
        communicationPreferences: []);
}
