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
    [Xunit.InlineData(true, false, "2", null, 0, 2, true, 2)]
    [Xunit.InlineData(false, true, null, "2", 2, 0, true, 2)]
    [Xunit.InlineData(true, true, "1", "2", 0, 0, true, null)]
    [Xunit.InlineData(true, true, "1,2", "1,2", 0, 0, true, 1)]
    [Xunit.InlineData(true, true, "1", "2", 0, 0, false, 0)]
    [Xunit.InlineData(false, false, null, null, 0, 0, true, 0)]
    public async Task Index_DuplicateWarningsCompareActualOccurrencesOncePerOtherEvent(
        bool queuedRecurring, bool otherRecurring, string? queuedDays, string? otherDays,
        int queuedStartDay, int otherStartDay, bool hasSettings, int? expectedDay)
    {
        var settings = BurnFixtures.Burn(year: 2026);
        var gate = settings.GateOpeningDate;
        var zone = DateTimeZoneProviders.Tzdb[settings.TimeZoneId];
        var pending = MakeEvent(Guid.NewGuid(), EventStatus.Pending) with
        {
            StartAt = gate.PlusDays(queuedStartDay).At(new LocalTime(10, 0)).InZoneLeniently(zone).ToInstant(),
            IsRecurring = queuedRecurring,
            RecurrenceDays = queuedDays
        };
        var other = new CampEventOverlap(Guid.NewGuid(), pending.CampId, "Other",
            gate.PlusDays(otherStartDay).At(new LocalTime(10, 30)).InZoneLeniently(zone).ToInstant(),
            60, EventStatus.Approved, otherRecurring, otherDays);
        if (hasSettings)
        {
            _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(new EventGuideSettingsView(
                Guid.NewGuid(), settings.Id, Instant.MinValue, Instant.MaxValue, Instant.MaxValue, 100,
                settings.TimeZoneId, Instant.MinValue, Instant.MinValue));
            _guide.GetEventSettingsByIdAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(settings);
        }
        _guide.GetEventsByStatusAsync(EventStatus.Pending, Arg.Any<CancellationToken>()).Returns([pending]);
        _guide.GetEventStatusCountsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<EventStatus, int>());
        _guide.GetCampEventsForOverlapAsync(Arg.Any<CancellationToken>()).Returns([
            other,
            other with { Id = pending.Id },
            other with { Id = Guid.NewGuid(), CampId = Guid.NewGuid() }
        ]);
        _camps.GetCampsForEventAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<CampInfo>());

        var view = (await BuildController(Guid.NewGuid(), pending.Id).Index(null, Xunit.TestContext.Current.CancellationToken))
            .Should().BeOfType<ViewResult>().Subject;
        var row = ((ModerationQueueViewModel)view.Model!).Events.Should().ContainSingle().Subject;

        if (expectedDay is { } day)
        {
            var candidate = row.DuplicateCandidates.Should().ContainSingle().Subject;
            candidate.Id.Should().Be(other.Id);
            var expected = gate.PlusDays(day).At(new LocalTime(10, 30));
            candidate.StartAt.Should().Be(hasSettings
                ? expected.ToDateTimeUnspecified()
                : other.StartAt.ToDateTimeUtc());
        }
        else
            row.DuplicateCandidates.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(nameof(EventsModerationController.Approve))]
    [Xunit.InlineData(nameof(EventsModerationController.Reject))]
    [Xunit.InlineData(nameof(EventsModerationController.RequestEdit))]
    [Xunit.InlineData(nameof(EventsModerationController.Withdraw))]
    public async Task MalformedModerationForm_IsRejectedBeforeEventReadsOrWrites(string action)
    {
        var moderatorId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));
        var controller = BuildController(moderatorId, eventId);
        var form = new ModerationActionFormModel { EventId = eventId, Reason = new string('x', 501) };
        controller.ModelState.AddModelError(nameof(form.Reason), "Reason exceeds 500 characters.");

        var result = action switch
        {
            nameof(EventsModerationController.Approve) => await controller.Approve(form),
            nameof(EventsModerationController.Reject) => await controller.Reject(form),
            nameof(EventsModerationController.RequestEdit) => await controller.RequestEdit(form),
            _ => await controller.Withdraw(form),
        };

        result.Should().BeOfType<BadRequestObjectResult>();
        await _guide.DidNotReceive().GetEventForModerationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _guide.DidNotReceive().ApplyModerationAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<EventModerationActionType>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _guide.DidNotReceive().ModeratorWithdrawAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Update_RejectsRecurrenceOutsideTheConfiguredEvent(bool isCampEvent)
    {
        var moderatorId = Guid.NewGuid();
        var settingsId = Guid.NewGuid();
        var guideEvent = MakeEvent(isCampEvent ? Guid.NewGuid() : null, EventStatus.Approved) with { Title = "Approved event" };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(new EventGuideSettingsView(
            Guid.NewGuid(), settingsId, Instant.MinValue, Instant.MaxValue, Instant.MaxValue, 100,
            "Europe/Madrid", Instant.MinValue, Instant.MinValue));
        _guide.GetEventSettingsByIdAsync(settingsId, Arg.Any<CancellationToken>()).Returns(new EventSettingsInfo(
            settingsId, "Burn", 2026, "Europe/Madrid", new LocalDate(2026, 8, 1),
            0, 2, 2, new Dictionary<int, int>(), null, null));
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));
        var controller = BuildController(moderatorId, guideEvent.Id);
        var model = new AdminEventFormViewModel
        {
            Title = "Changed",
            VenueId = Guid.NewGuid(),
            StartDate = new DateOnly(2026, 8, 1),
            IsRecurring = true,
            RecurrenceDays = "0,3"
        };

        var result = await controller.Update(guideEvent.Id, model);

        result.Should().BeOfType<ViewResult>();
        controller.ModelState[nameof(model.RecurrenceDays)]!.Errors.Should().ContainSingle();
        await _guide.DidNotReceive().AdminUpdateAsync(
            Arg.Any<Guid>(), Arg.Any<EventEdit>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Approve_pending_event_applies_the_approval_with_submitter_edit_url()
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = MakeEvent(null, EventStatus.Pending) with { Title = "Pending event" };
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

    [HumansTheory]
    [Xunit.InlineData(true, "A reason is required when rejecting an event.")]
    [Xunit.InlineData(false, "A reason is required when requesting edits.")]
    public async Task Missing_reason_refused_by_the_service_shows_the_reason_message(bool reject, string expected)
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = MakeEvent(null, EventStatus.Pending) with { Title = "Pending event" };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _guide.ApplyModerationAsync(guideEvent.Id, moderatorId, Arg.Any<EventModerationActionType>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ModerationOutcome.ReasonRequired);
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));
        var controller = BuildController(moderatorId, guideEvent.Id);
        var form = new ModerationActionFormModel { EventId = guideEvent.Id, Reason = " " };

        var result = reject ? await controller.Reject(form) : await controller.RequestEdit(form);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        controller.TempData[Humans.Base.Constants.TempDataKeys.ErrorMessage].Should().Be(expected);
    }

    [HumansFact]
    public async Task Non_pending_event_refused_by_the_service_shows_the_pending_message()
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = MakeEvent(null, EventStatus.Approved) with { Title = "Approved event" };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _guide.ApplyModerationAsync(guideEvent.Id, moderatorId, Arg.Any<EventModerationActionType>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ModerationOutcome.NotPending);
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));
        var controller = BuildController(moderatorId, guideEvent.Id);

        var result = await controller.Approve(new ModerationActionFormModel { EventId = guideEvent.Id });

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        controller.TempData[Humans.Base.Constants.TempDataKeys.ErrorMessage].Should().Be("This event is not in a pending state.");
    }

    [HumansFact]
    public async Task Unrelated_service_exception_propagates_instead_of_a_refusal_message()
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = MakeEvent(null, EventStatus.Pending) with { Title = "Pending event" };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _guide.ApplyModerationAsync(guideEvent.Id, moderatorId, Arg.Any<EventModerationActionType>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ModerationOutcome>(new InvalidOperationException("connection is not open")));
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));
        var controller = BuildController(moderatorId, guideEvent.Id);

        var act = () => controller.Approve(new ModerationActionFormModel { EventId = guideEvent.Id });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("connection is not open");
        controller.TempData.ContainsKey(Humans.Base.Constants.TempDataKeys.ErrorMessage).Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(EventStatus.Rejected, true)]
    [Xunit.InlineData(EventStatus.ResubmitRequested, true)]
    [Xunit.InlineData(EventStatus.Pending, false)]
    [Xunit.InlineData(EventStatus.Draft, false)]
    public void EventInfo_IsResubmission_only_after_moderation_sent_it_back(EventStatus status, bool expected)
        => MakeEvent(null, status).IsResubmission.Should().Be(expected);

    [HumansFact]
    public async Task Withdraw_approved_event_records_the_moderator()
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = MakeEvent(null, EventStatus.Approved) with { Title = "Approved event" };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));

        _guide.ModeratorWithdrawAsync(guideEvent.Id, moderatorId, Arg.Any<CancellationToken>()).Returns(true);
        var controller = BuildController(moderatorId, guideEvent.Id);

        await controller.Withdraw(new ModerationActionFormModel { EventId = guideEvent.Id });

        await _guide.Received(1).ModeratorWithdrawAsync(guideEvent.Id, moderatorId, Arg.Any<CancellationToken>());
        await _guide.DidNotReceive().WithdrawEventAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        controller.TempData["SuccessMessage"].Should().NotBeNull();
    }

    [HumansFact]
    public async Task Withdraw_leaves_the_status_rule_to_the_service_and_reports_its_refusal()
    {
        var moderatorId = Guid.NewGuid();
        var guideEvent = MakeEvent(null, EventStatus.Rejected) with { Title = "Rejected event" };
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(moderatorId)));
        _guide.ModeratorWithdrawAsync(guideEvent.Id, moderatorId, Arg.Any<CancellationToken>()).Returns(false);
        var controller = BuildController(moderatorId, guideEvent.Id);

        await controller.Withdraw(new ModerationActionFormModel { EventId = guideEvent.Id });

        await _guide.Received(1).ModeratorWithdrawAsync(guideEvent.Id, moderatorId, Arg.Any<CancellationToken>());
        controller.TempData["ErrorMessage"].Should().NotBeNull();
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
        _camps.GetCampsForEventAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<CampInfo>());
        _guide.GetCampEventsForOverlapAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<CampEventOverlap>());
        _guide.GetEventStatusCountsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<EventStatus, int>());
        _guide.GetEventsByStatusAsync(EventStatus.Pending, Arg.Any<CancellationToken>()).Returns([pending]);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [submitterId] = UserInfoFor(submitterId) });

        var controller = BuildController(moderatorId, pending.Id);

        using var request = new CancellationTokenSource();
        var result = await controller.Index(null, request.Token);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ModerationQueueViewModel>().Which;
        model.Events.Single().SubmitterUserId.Should().Be(submitterId);
        var history = model.Events.Single().History;
        history.Select(h => h.ActorUserId).Should().Equal(laterModeratorId, moderatorId);
        await _guide.Received(1).GetGuideSettingsAsync(request.Token);
        await _guide.Received(1).GetEventSettingsByIdAsync(settings.Id, request.Token);
        await _guide.Received(1).GetEventStatusCountsAsync(request.Token);
        await _guide.Received(1).GetEventsByStatusAsync(EventStatus.Pending, request.Token);
        await _guide.Received(1).GetCampEventsForOverlapAsync(request.Token);
        await _camps.Received(1).GetCampsForEventAsync(settings.Id, request.Token);
        await _users.DidNotReceive().GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Edit_PreservesCancellationThroughFormLookups(bool isCampEvent)
    {
        var categoryId = Guid.NewGuid();
        var venueId = Guid.NewGuid();
        var settings = BurnFixtures.Burn(year: 2026);
        var guideEvent = MakeEvent(isCampEvent ? Guid.NewGuid() : null, EventStatus.Pending) with
        {
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
        _camps.GetCampsForEventAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<CampInfo>());
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
            await _camps.Received(1).GetCampsForEventAsync(settings.Id, request.Token);
        else
        {
            await _guide.Received(1).GetActiveVenuesAsync(request.Token);
            await _guide.Received(1).GetVenueAsync(venueId, request.Token);
        }
    }

    [HumansTheory]
    [Xunit.InlineData("Approve", true, "actor")]
    [Xunit.InlineData("Approve", true, "event")]
    [Xunit.InlineData("Approve", true, "guide settings")]
    [Xunit.InlineData("Approve", true, "event settings")]
    [Xunit.InlineData("Approve", true, "camps")]
    [Xunit.InlineData("Reject", true, "actor")]
    [Xunit.InlineData("Reject", true, "event")]
    [Xunit.InlineData("Reject", true, "guide settings")]
    [Xunit.InlineData("Reject", true, "event settings")]
    [Xunit.InlineData("Reject", true, "camps")]
    [Xunit.InlineData("RequestEdit", true, "actor")]
    [Xunit.InlineData("RequestEdit", true, "event")]
    [Xunit.InlineData("RequestEdit", true, "guide settings")]
    [Xunit.InlineData("RequestEdit", true, "event settings")]
    [Xunit.InlineData("RequestEdit", true, "camps")]
    [Xunit.InlineData("Withdraw", true, "actor")]
    [Xunit.InlineData("Withdraw", true, "event")]
    [Xunit.InlineData("Update", false, "actor")]
    [Xunit.InlineData("Update", false, "event")]
    [Xunit.InlineData("Update", false, "guide settings")]
    [Xunit.InlineData("Update", false, "event settings")]
    [Xunit.InlineData("Update", false, "categories")]
    [Xunit.InlineData("Update", false, "category")]
    [Xunit.InlineData("Update", false, "venues")]
    [Xunit.InlineData("Update", false, "venue")]
    [Xunit.InlineData("Update", true, "actor")]
    [Xunit.InlineData("Update", true, "event")]
    [Xunit.InlineData("Update", true, "guide settings")]
    [Xunit.InlineData("Update", true, "event settings")]
    [Xunit.InlineData("Update", true, "camps")]
    [Xunit.InlineData("Update", true, "categories")]
    [Xunit.InlineData("Update", true, "category")]
    public async Task ModerationPreflight_StopsBeforeWritesWhenRequestIsCancelled(string action, bool isCampEvent, string cancelledRead)
    {
        using var request = new CancellationTokenSource();
        void Read(string name, CancellationToken ct)
        {
            if (!string.Equals(name, cancelledRead, StringComparison.Ordinal)) return;
            request.Cancel();
            ct.Should().Be(request.Token);
            ct.ThrowIfCancellationRequested();
        }

        var moderatorId = Guid.NewGuid();
        var settings = BurnFixtures.Burn(year: 2026);
        var guideEvent = MakeEvent(isCampEvent ? Guid.NewGuid() : null, EventStatus.Pending);
        var categoryId = guideEvent.CategoryId;
        var venueId = guideEvent.GuideSharedVenueId ?? Guid.NewGuid();
        _users.GetUserInfoAsync(moderatorId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("actor", call.Arg<CancellationToken>());
            return new ValueTask<UserInfo?>(UserInfoFor(moderatorId));
        });
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("event", call.Arg<CancellationToken>());
            return guideEvent;
        });
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("guide settings", call.Arg<CancellationToken>());
            return new EventGuideSettingsView(Guid.NewGuid(), settings.Id, Instant.MinValue, Instant.MaxValue,
                Instant.MaxValue, 100, settings.TimeZoneId, Instant.MinValue, Instant.MinValue);
        });
        _guide.GetEventSettingsByIdAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("event settings", call.Arg<CancellationToken>());
            return settings;
        });
        _camps.GetCampsForEventAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("camps", call.Arg<CancellationToken>());
            return Array.Empty<CampInfo>();
        });
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("categories", call.Arg<CancellationToken>());
            return Array.Empty<EventCategoryView>();
        });
        _guide.GetCategoryAsync(categoryId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("category", call.Arg<CancellationToken>());
            return new EventCategoryView(categoryId, "Old", "old", false, 0, false);
        });
        _guide.GetActiveVenuesAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("venues", call.Arg<CancellationToken>());
            return Array.Empty<EventVenueView>();
        });
        _guide.GetVenueAsync(venueId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("venue", call.Arg<CancellationToken>());
            return new EventVenueView(venueId, "Old venue", null, null, 0, false);
        });
        var controller = BuildController(moderatorId, guideEvent.Id);
        controller.HttpContext.RequestAborted = request.Token;
        var form = new ModerationActionFormModel { EventId = guideEvent.Id, Reason = "Reason" };
        if (string.Equals(action, "Update", StringComparison.Ordinal)) controller.ModelState.AddModelError("Title", "Required");
        Func<Task<IActionResult>> act = () => action switch
        {
            "Approve" => controller.Approve(form),
            "Reject" => controller.Reject(form),
            "RequestEdit" => controller.RequestEdit(form),
            "Withdraw" => controller.Withdraw(form),
            _ => controller.Update(guideEvent.Id, new AdminEventFormViewModel
            {
                CategoryId = categoryId,
                VenueId = venueId,
                StartDate = new DateOnly(2026, 8, 1)
            })
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        await _guide.DidNotReceive().ApplyModerationAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<EventModerationActionType>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _guide.DidNotReceive().ModeratorWithdrawAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _guide.DidNotReceive().AdminUpdateAsync(Arg.Any<Guid>(), Arg.Any<EventEdit>(), Arg.Any<Guid>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
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

    private static EventInfo MakeEvent(Guid? campId, EventStatus status) => new(
        Guid.NewGuid(), campId, campId == null ? Guid.NewGuid() : null, Guid.NewGuid(), Guid.NewGuid(),
        "Music", "music", false, null, "Test Event", "Description", null, null,
        Instant.FromUtc(2026, 8, 1, 18, 0), 60, false, null, null, status,
        Instant.FromUtc(2026, 7, 1, 0, 0), Instant.FromUtc(2026, 7, 1, 0, 0), []);

    private static UserInfo UserInfoFor(Guid userId) => UserInfo.Create(
        new User { Id = userId, DisplayName = "Moderator", PreferredLanguage = "en", CreatedAt = Instant.MinValue },
        userEmails: [],
        externalLogins: [],
        profile: UserFixtures.Profile(burnerName: "Moderator", firstName: "Mod", lastName: "Erator"));
}
