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
using Humans.Settings.Contracts;
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
    private readonly IEventManagement _guide = Substitute.For<IEventManagement>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ICampService _camps = Substitute.For<ICampService>();

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Update_RejectsRecurrenceOutsideTheConfiguredEvent(bool isCampEvent)
    {
        var moderatorId = Guid.NewGuid();
        var settingsId = Guid.NewGuid();
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            CampId = isCampEvent ? Guid.NewGuid() : null,
            Title = "Approved event",
            Status = EventStatus.Approved
        };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(new EventGuideSettingsView(
            Guid.NewGuid(), settingsId, Instant.MinValue, Instant.MaxValue, Instant.MaxValue, 100,
            "Europe/Madrid", Instant.MinValue, Instant.MinValue));
        _guide.GetEventSettingsByIdAsync(settingsId, Arg.Any<CancellationToken>()).Returns(new EventSettingsInfo(
            settingsId, "Burn", 2026, "Europe/Madrid", new LocalDate(2026, 8, 1),
            0, 2, 2, 0, 0, 0, 0, new Dictionary<int, int>(), null, null));
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));
        var controller = BuildController(moderatorId, guideEvent.Id);
        var model = new AdminEventFormViewModel
        {
            Title = "Changed",
            VenueId = Guid.NewGuid(),
            StartDate = new DateTime(2026, 8, 1),
            IsRecurring = true,
            RecurrenceDays = "0,3"
        };

        var result = await controller.Update(guideEvent.Id, model);

        result.Should().BeOfType<ViewResult>();
        controller.ModelState[nameof(model.RecurrenceDays)]!.Errors.Should().ContainSingle();
        guideEvent.Title.Should().Be("Approved event");
        await _guide.DidNotReceive().AdminUpdateAsync(Arg.Any<Event>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

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
    public async Task Withdraw_approved_event_records_the_moderator()
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = new Event { Id = Guid.NewGuid(), Title = "Approved event", Status = EventStatus.Approved };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));

        var controller = BuildController(moderatorId, guideEvent.Id);

        await controller.Withdraw(new ModerationActionFormModel { EventId = guideEvent.Id });

        await _guide.Received(1).ModeratorWithdrawAsync(guideEvent, moderatorId, Arg.Any<CancellationToken>());
        await _guide.DidNotReceive().WithdrawEventAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_carries_the_actor_of_each_history_entry_newest_first()
    {
        var moderatorId = Guid.NewGuid();
        var laterModeratorId = Guid.NewGuid();
        var submitterId = Guid.NewGuid();
        var pending = new EventInfo(
            Guid.NewGuid(), Guid.NewGuid(), null, submitterId, Guid.NewGuid(), "Music", "music", false, null,
            "Pending event", "Description", null, null, Instant.FromUtc(2026, 8, 1, 18, 0), 60,
            false, null, null, EventStatus.Pending, Instant.FromUtc(2026, 7, 1, 0, 0), Instant.FromUtc(2026, 7, 1, 0, 0),
            [
                new EventModerationHistoryInfo(moderatorId, EventModerationActionType.ResubmitRequested, "Fix the time", Instant.FromUtc(2026, 7, 2, 0, 0)),
                new EventModerationHistoryInfo(laterModeratorId, EventModerationActionType.Approved, null, Instant.FromUtc(2026, 7, 3, 0, 0)),
            ]);
        var settings = BurnFixtures.Burn(year: 2026);
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(new EventGuideSettingsView(
            Guid.NewGuid(), settings.Id, Instant.MinValue, Instant.MaxValue, Instant.MaxValue, 100,
            settings.TimeZoneId, Instant.MinValue, Instant.MinValue));
        _guide.GetEventSettingsByIdAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(settings);
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(Array.Empty<CampInfo>());
        _guide.GetCampEventsForOverlapAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<CampEventOverlap>());
        _guide.GetEventStatusCountsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<EventStatus, int>());
        _guide.GetEventsByStatusAsync(EventStatus.Pending, Arg.Any<CancellationToken>()).Returns([pending]);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [submitterId] = UserInfoFor(submitterId) });

        var controller = BuildController(moderatorId, pending.Id);

        using var request = new CancellationTokenSource();
        var result = await controller.Index(null, request.Token);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ModerationQueueViewModel>().Which;
        var history = model.Events.Single().History;
        history.Select(h => h.ActorUserId).Should().Equal(laterModeratorId, moderatorId);
        await _guide.Received(1).GetGuideSettingsAsync(request.Token);
        await _guide.Received(1).GetEventSettingsByIdAsync(settings.Id, request.Token);
        await _guide.Received(1).GetEventStatusCountsAsync(request.Token);
        await _guide.Received(1).GetEventsByStatusAsync(EventStatus.Pending, request.Token);
        await _guide.Received(1).GetCampEventsForOverlapAsync(request.Token);
        await _camps.Received(1).GetCampsForYearAsync(2026, request.Token);
        await _users.Received(1).GetUserInfosAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(submitterId)), request.Token);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Edit_PreservesCancellationThroughFormLookups(bool isCampEvent)
    {
        var categoryId = Guid.NewGuid();
        var venueId = Guid.NewGuid();
        var settings = BurnFixtures.Burn(year: 2026);
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            CampId = isCampEvent ? Guid.NewGuid() : null,
            CategoryId = categoryId,
            GuideSharedVenueId = venueId,
            StartAt = Instant.FromUtc(2026, 8, 1, 12, 0),
            Title = "Event"
        };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(new EventGuideSettingsView(
            Guid.NewGuid(), settings.Id, Instant.MinValue, Instant.MaxValue, Instant.MaxValue, 100,
            settings.TimeZoneId, Instant.MinValue, Instant.MinValue));
        _guide.GetEventSettingsByIdAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(settings);
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<EventCategoryView>());
        _guide.GetCategoryAsync(categoryId, Arg.Any<CancellationToken>())
            .Returns(new EventCategoryView(categoryId, "Old", "old", false, 0, false));
        _guide.GetActiveVenuesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<EventVenueView>());
        _guide.GetVenueAsync(venueId, Arg.Any<CancellationToken>())
            .Returns(new EventVenueView(venueId, "Old venue", null, null, 0, false));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(Array.Empty<CampInfo>());
        var controller = BuildController(Guid.NewGuid(), guideEvent.Id);
        using var request = new CancellationTokenSource();

        var result = await controller.Edit(guideEvent.Id, request.Token);

        result.Should().BeOfType<ViewResult>();
        await _guide.Received(1).GetEventForModerationAsync(guideEvent.Id, request.Token);
        await _guide.Received(1).GetGuideSettingsAsync(request.Token);
        await _guide.Received(1).GetEventSettingsByIdAsync(settings.Id, request.Token);
        await _guide.Received(1).GetActiveCategoriesAsync(request.Token);
        await _guide.Received(1).GetCategoryAsync(categoryId, request.Token);
        if (isCampEvent)
            await _camps.Received(1).GetCampsForYearAsync(2026, request.Token);
        else
        {
            await _guide.Received(1).GetActiveVenuesAsync(request.Token);
            await _guide.Received(1).GetVenueAsync(venueId, request.Token);
        }
    }

    private EventsModerationController BuildController(Guid moderatorId, Guid eventId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, moderatorId.ToString())], "test")),
        };
        var controller = new EventsModerationController(
            _guide, _users, _camps,
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
