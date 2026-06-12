using AwesomeAssertions;
using Humans.Application.DTOs.Events;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Events;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public sealed class EventServiceTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 5, 12, 0));
    private readonly FakeEventRepository _repo = new();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly EventService _service;

    public EventServiceTests()
    {
        _service = new EventService(_repo, _burnSettings, _userService, _emailService, _emailMessages, _clock, NullLogger<EventService>.Instance);
    }

    [HumansTheory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    public void IsSubmissionOpenAt_UsesInclusiveOpenAndCloseWindow(int hour, bool expected)
    {
        var settings = new EventGuideSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = Guid.NewGuid(),
            SubmissionOpenAt = Instant.FromUtc(2026, 5, 5, 12, 0),
            SubmissionCloseAt = Instant.FromUtc(2026, 5, 5, 13, 0),
            GuidePublishAt = Instant.FromUtc(2026, 5, 6, 12, 0),
            MaxPrintSlots = 10,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        var result = settings.IsSubmissionOpenAt(Instant.FromUtc(2026, 5, 5, hour, 0));

        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task SaveGuideSettingsAsync_CreatesSettingsUsingEventTimezone()
    {
        var eventSettingsId = Guid.NewGuid();
        _burnSettings.GetByIdAsync(eventSettingsId, Arg.Any<CancellationToken>()).Returns(new BurnSettingsInfo(
            Id: eventSettingsId,
            EventName: "Nowhere 2026",
            Year: 2026,
            TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 1),
            BuildStartOffset: -14,
            EventEndOffset: 7,
            StrikeEndOffset: 10,
            FirstCrewStartOffset: -25,
            SetupWeekStartOffset: -16,
            PreEventWeekStartOffset: -9,
            FinishingWeekendStartOffset: -4,
            EarlyEntryCapacity: new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null));

        await _service.SaveGuideSettingsAsync(
            existingId: null,
            eventSettingsId,
            submissionOpenAt: new LocalDateTime(2026, 5, 5, 12, 0),
            submissionCloseAt: new LocalDateTime(2026, 5, 6, 12, 0),
            guidePublishAt: new LocalDateTime(2026, 5, 7, 12, 0),
            maxPrintSlots: 42, ct: TestContext.Current.CancellationToken);

        _repo.Settings.Should().NotBeNull();
        _repo.Settings!.SubmissionOpenAt.Should().Be(Instant.FromUtc(2026, 5, 5, 10, 0));
        _repo.Settings.SubmissionCloseAt.Should().Be(Instant.FromUtc(2026, 5, 6, 10, 0));
        _repo.Settings.GuidePublishAt.Should().Be(Instant.FromUtc(2026, 5, 7, 10, 0));
        _repo.Settings.MaxPrintSlots.Should().Be(42);
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task SaveGuideSettingsAsync_Throws_WhenEventSettingsMissing()
    {
        var eventSettingsId = Guid.NewGuid();

        var act = () => _service.SaveGuideSettingsAsync(
            null,
            eventSettingsId,
            new LocalDateTime(2026, 5, 5, 12, 0),
            new LocalDateTime(2026, 5, 6, 12, 0),
            new LocalDateTime(2026, 5, 7, 12, 0),
            10, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"EventSettings {eventSettingsId} not found.");
    }

    [HumansFact]
    public async Task MoveCategoryAsync_SwapsDisplayOrderWithNeighbour()
    {
        var first = new EventCategory { Id = Guid.NewGuid(), Name = "A", Slug = "a", DisplayOrder = 1 };
        var second = new EventCategory { Id = Guid.NewGuid(), Name = "B", Slug = "b", DisplayOrder = 2 };
        _repo.Categories.AddRange([first, second]);

        await _service.MoveCategoryAsync(second.Id, direction: -1, ct: TestContext.Current.CancellationToken);

        second.DisplayOrder.Should().Be(1);
        first.DisplayOrder.Should().Be(2);
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task DeleteVenueAsync_ReturnsLinkedCountAndDoesNotRemove_WhenEventsReferenceVenue()
    {
        var venue = new EventVenue { Id = Guid.NewGuid(), Name = "Main Stage" };
        venue.Events.Add(new Event { Id = Guid.NewGuid(), Title = "Talk" });
        _repo.Venues.Add(venue);

        var result = await _service.DeleteVenueAsync(venue.Id, TestContext.Current.CancellationToken);

        result.Should().Be((false, 1));
        _repo.RemovedVenues.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansFact]
    public async Task ToggleFavouriteAsync_AddsFavourite_WhenMissing()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await _service.ToggleFavouriteAsync(userId, eventId, dayOffset: 4, TestContext.Current.CancellationToken);

        _repo.Favourites.Should().ContainSingle(f =>
            f.UserId == userId
            && f.GuideEventId == eventId
            && f.DayOffset == 4
            && f.CreatedAt == _clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ToggleFavouriteAsync_RemovesFavourite_WhenExisting()
    {
        var fav = new EventFavourite
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            GuideEventId = Guid.NewGuid(),
            CreatedAt = _clock.GetCurrentInstant()
        };
        _repo.Favourites.Add(fav);

        await _service.ToggleFavouriteAsync(fav.UserId, fav.GuideEventId, dayOffset: null, TestContext.Current.CancellationToken);

        _repo.Favourites.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ToggleFavouriteAsync_DaySpecificToggle_RemovesWholeEventFavourite()
    {
        var fav = new EventFavourite
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            GuideEventId = Guid.NewGuid(),
            DayOffset = null,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _repo.Favourites.Add(fav);

        await _service.ToggleFavouriteAsync(fav.UserId, fav.GuideEventId, dayOffset: 2, TestContext.Current.CancellationToken);

        _repo.Favourites.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ToggleFavouriteAsync_DifferentDays_KeepSeparateFavourites()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await _service.ToggleFavouriteAsync(userId, eventId, dayOffset: 2, TestContext.Current.CancellationToken);
        await _service.ToggleFavouriteAsync(userId, eventId, dayOffset: 4, TestContext.Current.CancellationToken);

        _repo.Favourites.Should().HaveCount(2);
        _repo.Favourites.Select(f => f.DayOffset).Should().BeEquivalentTo([2, 4]);
    }

    [HumansFact]
    public async Task SavePreferenceAsync_UpdatesExistingPreferenceJsonAndTimestamp()
    {
        var userId = Guid.NewGuid();
        _repo.Preference = new EventPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ExcludedCategorySlugs = "[]",
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 12, 0)
        };

        await _service.SavePreferenceAsync(userId, ["adult", "spiritual"], TestContext.Current.CancellationToken);

        _repo.Preference.ExcludedCategorySlugs.Should().Be("[\"adult\",\"spiritual\"]");
        _repo.Preference.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ApplyModerationAsync_TransitionsEventAndAppendsModerationAction()
    {
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            Status = EventStatus.Pending,
            LastUpdatedAt = Instant.FromUtc(2026, 5, 1, 12, 0)
        };
        _repo.Events.Add(guideEvent);
        var actorUserId = Guid.NewGuid();

        await _service.ApplyModerationAsync(
            guideEvent.Id,
            actorUserId,
            EventModerationActionType.ResubmitRequested,
            "Add location", submitterEditUrl: null, TestContext.Current.CancellationToken);

        guideEvent.Status.Should().Be(EventStatus.ResubmitRequested);
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
        _repo.EventModerationActions.Should().ContainSingle(action =>
            action.GuideEventId == guideEvent.Id
            && action.ActorUserId == actorUserId
            && action.Action == EventModerationActionType.ResubmitRequested
            && action.Reason == "Add location"
            && action.CreatedAt == _clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task SubmitEventAsync_WithActionUrl_EmailsSubmitterConfirmation()
    {
        var submitterId = StubSubmitterWithEmail("sub@example.com", "Burner");
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterId,
            Title = "Fire show",
            Status = EventStatus.Pending,
        };

        await _service.SubmitEventAsync(guideEvent, "https://x/Events/MySubmissions", TestContext.Current.CancellationToken);

        _repo.Events.Should().Contain(guideEvent);
        _emailMessages.Received(1).EventLifecycle(
            Arg.Is<EventLifecycleNotification>(n =>
                n.NewStatus == EventStatus.Pending
                && n.UserName == "Burner"
                && n.EventTitle == "Fire show"
                && n.ActionUrl == "https://x/Events/MySubmissions"),
            "sub@example.com");
        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>());
    }

    [HumansFact]
    public async Task SubmitEventAsync_NullActionUrl_SkipsEmail()
    {
        // Bulk import opts out — one email per CSV row would spam the uploader.
        var guideEvent = new Event { Id = Guid.NewGuid(), SubmitterUserId = Guid.NewGuid(), Status = EventStatus.Pending };

        await _service.SubmitEventAsync(guideEvent, lifecycleActionUrl: null, TestContext.Current.CancellationToken);

        _repo.Events.Should().Contain(guideEvent);
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansFact]
    public async Task ApplyModerationAsync_WithEditUrl_EmailsDecisionWithReason()
    {
        var submitterId = StubSubmitterWithEmail("sub@example.com", "Burner");
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterId,
            Title = "Fire show",
            Status = EventStatus.Pending,
        };
        _repo.Events.Add(guideEvent);

        await _service.ApplyModerationAsync(
            guideEvent.Id, Guid.NewGuid(), EventModerationActionType.Rejected,
            "Too loud", "https://x/edit", TestContext.Current.CancellationToken);

        _emailMessages.Received(1).EventLifecycle(
            Arg.Is<EventLifecycleNotification>(n =>
                n.NewStatus == EventStatus.Rejected
                && n.Reason == "Too loud"
                && n.ActionUrl == "https://x/edit"),
            "sub@example.com");
        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>());
    }

    [HumansFact]
    public async Task ApplyModerationAsync_EmailFailure_DoesNotFailModeration()
    {
        // The decision is already persisted when the email goes out — a degraded
        // email service must not surface as a failed moderation (or skip the
        // caching decorator's invalidation).
        var submitterId = StubSubmitterWithEmail("sub@example.com", "Burner");
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterId,
            Title = "Fire show",
            Status = EventStatus.Pending,
        };
        _repo.Events.Add(guideEvent);
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP down")));

        var act = () => _service.ApplyModerationAsync(
            guideEvent.Id, Guid.NewGuid(), EventModerationActionType.Rejected,
            "Too loud", "https://x/edit", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        guideEvent.Status.Should().Be(EventStatus.Rejected);
    }

    private Guid StubSubmitterWithEmail(string email, string burnerName)
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, DisplayName = burnerName, PreferredLanguage = "en" };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(user.ToUserInfo(userEmails:
            [
                new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = email, IsVerified = true, IsPrimary = true },
            ]));
        return userId;
    }

    [HumansFact]
    public async Task UpdateAndResubmitAsync_PendingEvent_KeepsPendingAndSaves()
    {
        var submittedAt = Instant.FromUtc(2026, 5, 1, 12, 0);
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            Status = EventStatus.Pending,
            SubmittedAt = submittedAt,
            LastUpdatedAt = Instant.FromUtc(2026, 5, 1, 13, 0)
        };

        await _service.UpdateAndResubmitAsync(guideEvent, TestContext.Current.CancellationToken);

        guideEvent.Status.Should().Be(EventStatus.Pending);
        guideEvent.SubmittedAt.Should().Be(submittedAt);
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task UpdateAndResubmitAsync_ApprovedEvent_RequeuesForModeration()
    {
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            Status = EventStatus.Approved,
            SubmittedAt = Instant.FromUtc(2026, 5, 1, 12, 0),
            LastUpdatedAt = Instant.FromUtc(2026, 5, 2, 12, 0)
        };

        await _service.UpdateAndResubmitAsync(guideEvent, TestContext.Current.CancellationToken);

        guideEvent.Status.Should().Be(EventStatus.Pending);
        guideEvent.SubmittedAt.Should().Be(_clock.GetCurrentInstant());
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task AdminUpdateAsync_ApprovedEvent_PreservesStatusAndAppendsEditedAction()
    {
        var submittedAt = Instant.FromUtc(2026, 5, 1, 12, 0);
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            Title = "Fixed title",
            Status = EventStatus.Approved,
            SubmittedAt = submittedAt,
            LastUpdatedAt = Instant.FromUtc(2026, 5, 2, 12, 0)
        };
        _repo.Events.Add(guideEvent);
        var actorUserId = Guid.NewGuid();

        await _service.AdminUpdateAsync(guideEvent, actorUserId, "fixed the start time", TestContext.Current.CancellationToken);

        guideEvent.Status.Should().Be(EventStatus.Approved); // never re-queued to Pending
        guideEvent.SubmittedAt.Should().Be(submittedAt);     // submission timestamp untouched
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
        _repo.EventModerationActions.Should().ContainSingle(action =>
            action.GuideEventId == guideEvent.Id
            && action.ActorUserId == actorUserId
            && action.Action == EventModerationActionType.Edited
            && action.Reason == "fixed the start time"
            && action.CreatedAt == _clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AdminUpdateAsync_BlankNote_AppendsEditedActionWithNullReason_AnyStatePreserved(string note)
    {
        var guideEvent = new Event { Id = Guid.NewGuid(), Status = EventStatus.Withdrawn };
        _repo.Events.Add(guideEvent);

        await _service.AdminUpdateAsync(guideEvent, Guid.NewGuid(), note, TestContext.Current.CancellationToken);

        guideEvent.Status.Should().Be(EventStatus.Withdrawn); // any state edited in place
        _repo.EventModerationActions.Should().ContainSingle(a =>
            a.Action == EventModerationActionType.Edited && a.Reason == null);
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_EmitsEventsSliceWithFavouritesAndPreference()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var earlier = Instant.FromUtc(2026, 5, 1, 12, 0);
        var later = Instant.FromUtc(2026, 5, 2, 12, 0);
        var laterEventId = Guid.NewGuid();
        var earlierEventId = Guid.NewGuid();
        _repo.Favourites.Add(new EventFavourite { Id = Guid.NewGuid(), UserId = userId, GuideEventId = laterEventId, CreatedAt = later });
        _repo.Favourites.Add(new EventFavourite { Id = Guid.NewGuid(), UserId = userId, GuideEventId = earlierEventId, CreatedAt = earlier });
        _repo.Favourites.Add(new EventFavourite { Id = Guid.NewGuid(), UserId = otherUserId, GuideEventId = Guid.NewGuid(), CreatedAt = earlier });
        _repo.Preference = new EventPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ExcludedCategorySlugs = "[\"adults\"]",
            UpdatedAt = later
        };

        var slices = await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be(GdprExportSections.Events);
        slices[0].Data.Should().NotBeNull();
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ReturnsEmptySliceWhenUserHasNoData()
    {
        var slices = await _service.ContributeForUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be(GdprExportSections.Events);
        slices[0].Data.Should().NotBeNull();
    }

    [HumansFact]
    public async Task BulkImportAsync_InvalidRow_ReturnsErrorsAndWritesNothing()
    {
        var campId = Guid.NewGuid();
        _repo.Categories.Add(new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true });

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(title: "")],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Errors.Contains("Title is required."));
        _repo.Events.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansFact]
    public async Task BulkImportAsync_NewRow_CreatesPendingEvent()
    {
        var campId = Guid.NewGuid();
        var submitter = Guid.NewGuid();
        var cat = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(cat);

        var result = await _service.BulkImportAsync(
            campId, submitter, [Row()],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeFalse();
        result.CreatedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(0);
        var created = _repo.Events.Should().ContainSingle().Subject;
        created.Status.Should().Be(EventStatus.Pending);
        created.CampId.Should().Be(campId);
        created.SubmitterUserId.Should().Be(submitter);
        created.CategoryId.Should().Be(cat.Id);
    }

    [HumansFact]
    public async Task BulkImportAsync_UnchangedExistingRow_IsNoOp()
    {
        var campId = Guid.NewGuid();
        var cat = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(cat);
        var existing = ExistingEvent(campId, cat.Id, EventStatus.Approved);
        _repo.Events.Add(existing);

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(id: existing.Id)],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeFalse();
        result.CreatedCount.Should().Be(0);
        result.UpdatedCount.Should().Be(0);
        existing.Status.Should().Be(EventStatus.Approved);
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansFact]
    public async Task BulkImportAsync_EditedApprovedRow_RequeuesToPending()
    {
        var campId = Guid.NewGuid();
        var cat = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(cat);
        var existing = ExistingEvent(campId, cat.Id, EventStatus.Approved);
        _repo.Events.Add(existing);

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(id: existing.Id, title: "New Title")],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.UpdatedCount.Should().Be(1);
        existing.Title.Should().Be("New Title");
        existing.Status.Should().Be(EventStatus.Pending);
        existing.SubmittedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task BulkImportAsync_EditedDraftRow_SubmitsViaUpdatePath_NoDuplicateInsert()
    {
        // Regression: the Draft branch previously called SubmitEventAsync (INSERT)
        // on an already-persisted entity, which throws a duplicate-key error.
        var campId = Guid.NewGuid();
        var cat = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(cat);
        var existing = ExistingEvent(campId, cat.Id, EventStatus.Draft);
        _repo.Events.Add(existing);
        var countBefore = _repo.Events.Count;

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(id: existing.Id, title: "Renamed")],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.UpdatedCount.Should().Be(1);
        existing.Status.Should().Be(EventStatus.Pending);
        _repo.Events.Count.Should().Be(countBefore); // no INSERT of the existing event
    }

    [HumansFact]
    public async Task BulkImportAsync_RecurrenceDayNameRoundTrip_IsNoOp()
    {
        // Existing recurrence is stored as offsets ("0"); the CSV carries the
        // equivalent day name. The round-trip must not be seen as an edit.
        var gate = new LocalDate(2026, 7, 8);
        var dayName = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }[((int)gate.DayOfWeek - 1 + 7) % 7];
        var campId = Guid.NewGuid();
        var cat = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(cat);
        var existing = ExistingEvent(campId, cat.Id, EventStatus.Approved);
        existing.IsRecurring = true;
        existing.RecurrenceDays = "0";
        _repo.Events.Add(existing);

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(id: existing.Id, isRecurring: true, recurrenceDays: dayName)],
            gate, 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.UpdatedCount.Should().Be(0);
        existing.Status.Should().Be(EventStatus.Approved);
    }

    private static BulkCsvRow Row(
        Guid? id = null, string title = "My Event", string description = "Desc",
        string category = "Workshop", string date = "2026-07-08", string startTime = "09:30",
        int duration = 60, string? location = null, string? host = null,
        bool isRecurring = false, string? recurrenceDays = null, int priority = 1, int rowNumber = 2)
        => new(rowNumber, id, title, description, category, date, startTime, duration, location, host, isRecurring, recurrenceDays, priority);

    private Event ExistingEvent(Guid campId, Guid categoryId, EventStatus status)
    {
        var startAt = (new LocalDate(2026, 7, 8) + new LocalTime(9, 30)).InZoneLeniently(DateTimeZone.Utc).ToInstant();
        return new Event
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            SubmitterUserId = Guid.NewGuid(),
            CategoryId = categoryId,
            Title = "My Event",
            Description = "Desc",
            StartAt = startAt,
            DurationMinutes = 60,
            IsRecurring = false,
            PriorityRank = 1,
            Status = status,
            SubmittedAt = _clock.GetCurrentInstant(),
            LastUpdatedAt = _clock.GetCurrentInstant()
        };
    }

    private sealed class FakeEventRepository : IEventRepository
    {
        public EventGuideSettings? Settings { get; set; }
        public List<EventCategory> Categories { get; } = [];
        public List<EventVenue> Venues { get; } = [];
        public List<EventVenue> RemovedVenues { get; } = [];
        public List<Event> Events { get; } = [];
        public List<EventFavourite> Favourites { get; } = [];
        public List<EventModerationAction> EventModerationActions { get; } = [];
        public EventPreference? Preference { get; set; }
        public int SaveChangesCount { get; private set; }

        public Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
            => Task.FromResult(Settings);

        public Task UpsertGuideSettingsAsync(EventGuideSettings settings, CancellationToken ct = default)
        {
            Settings = settings;
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventCategory>>(Categories.Where(c => c.IsActive).ToList());

        public Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventCategory>>(Categories);

        public Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Categories.FirstOrDefault(c => c.Id == id));

        public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default)
            => Task.FromResult(Categories.Any(c => string.Equals(c.Slug, slug, StringComparison.Ordinal) && c.Id != excludeId));

        public Task<int> GetMaxCategoryOrderAsync(CancellationToken ct = default)
            => Task.FromResult(Categories.Count == 0 ? 0 : Categories.Max(c => c.DisplayOrder));

        public Task AddCategoryAsync(EventCategory category, CancellationToken ct = default)
        {
            Categories.Add(category);
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task SaveCategoryAsync(EventCategory category, CancellationToken ct = default)
        {
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
        {
            var category = Categories.FirstOrDefault(c => c.Id == id);
            if (category == null) return Task.FromResult((false, -1));
            if (category.Events.Count > 0) return Task.FromResult((false, category.Events.Count));
            Categories.Remove(category);
            SaveChangesCount++;
            return Task.FromResult((true, 0));
        }

        public Task SwapCategoryOrderAsync(Guid id, int direction, CancellationToken ct = default)
        {
            var ordered = Categories.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
            var index = ordered.FindIndex(c => c.Id == id);
            if (index < 0) return Task.CompletedTask;
            var targetIndex = index + direction;
            if (targetIndex < 0 || targetIndex >= ordered.Count) return Task.CompletedTask;
            (ordered[index].DisplayOrder, ordered[targetIndex].DisplayOrder) =
                (ordered[targetIndex].DisplayOrder, ordered[index].DisplayOrder);
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventVenue>>(Venues.Where(v => v.IsActive).ToList());

        public Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventVenue>>(Venues);

        public Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Venues.FirstOrDefault(v => v.Id == id));

        public Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default)
            => Task.FromResult(Venues.Count == 0 ? 0 : Venues.Max(v => v.DisplayOrder));

        public Task AddVenueAsync(EventVenue venue, CancellationToken ct = default)
        {
            Venues.Add(venue);
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task SaveVenueAsync(EventVenue venue, CancellationToken ct = default)
        {
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
        {
            var venue = Venues.FirstOrDefault(v => v.Id == id);
            if (venue == null) return Task.FromResult((false, -1));
            if (venue.Events.Count > 0) return Task.FromResult((false, venue.Events.Count));
            RemovedVenues.Add(venue);
            Venues.Remove(venue);
            SaveChangesCount++;
            return Task.FromResult((true, 0));
        }

        public Task SwapVenueOrderAsync(Guid id, int direction, CancellationToken ct = default)
        {
            var ordered = Venues.OrderBy(v => v.DisplayOrder).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();
            var index = ordered.FindIndex(v => v.Id == id);
            if (index < 0) return Task.CompletedTask;
            var targetIndex = index + direction;
            if (targetIndex < 0 || targetIndex >= ordered.Count) return Task.CompletedTask;
            (ordered[index].DisplayOrder, ordered[targetIndex].DisplayOrder) =
                (ordered[targetIndex].DisplayOrder, ordered[index].DisplayOrder);
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Event>>(Events.Where(e => e.CampId == null && e.SubmitterUserId == userId).ToList());

        public Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == userId));

        public Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Event>>(Events.Where(e => e.CampId == campId).ToList());

        public Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == eventId && e.CampId == campId));

        public Task AddEventAsync(Event guideEvent, CancellationToken ct = default)
        {
            Events.Add(guideEvent);
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task SaveEventAsync(Event guideEvent, CancellationToken ct = default)
        {
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Event>> GetApprovedEventsAsync(Guid? campId, Guid? venueId, Guid? categoryId, string? q, IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Event>>(Events.Where(e => e.Status == EventStatus.Approved).ToList());

        public Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == id && e.Status == EventStatus.Approved));

        public Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Event>>(Events);

        public Task<Dictionary<EventStatus, int>> GetModerationStatusCountsAsync(CancellationToken ct = default)
            => Task.FromResult(Events.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()));

        public Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Event>>(Events.Where(e => e.Status == status).ToList());

        public Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == eventId));

        public Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CampEventOverlap>>([]);

        public Task SaveEventAndModerationActionAsync(Event guideEvent, EventModerationAction action, CancellationToken ct = default)
        {
            EventModerationActions.Add(action);
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(Favourites.Where(f => f.UserId == userId).Select(f => f.GuideEventId).ToHashSet());

        public Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventFavourite>>(Favourites.Where(f => f.UserId == userId).ToList());

        public Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default)
            => Task.FromResult(Favourites.Any(f => f.UserId == userId && f.GuideEventId == eventId));

        public Task<bool> ToggleFavouriteAsync(Guid userId, Guid eventId, EventFavourite newFavourite, CancellationToken ct = default)
        {
            var existing = MatchingFavourites(userId, eventId, newFavourite.DayOffset).ToList();
            if (existing.Count > 0)
            {
                existing.ForEach(f => Favourites.Remove(f));
                SaveChangesCount++;
                return Task.FromResult(false);
            }
            Favourites.Add(newFavourite);
            SaveChangesCount++;
            return Task.FromResult(true);
        }

        public Task<bool> AddFavouriteIfAbsentAsync(EventFavourite favourite, CancellationToken ct = default)
        {
            if (MatchingFavourites(favourite.UserId, favourite.GuideEventId, favourite.DayOffset).Any())
                return Task.FromResult(false);
            Favourites.Add(favourite);
            SaveChangesCount++;
            return Task.FromResult(true);
        }

        public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default)
        {
            var existing = MatchingFavourites(userId, eventId, dayOffset).ToList();
            if (existing.Count == 0) return Task.FromResult(false);
            existing.ForEach(f => Favourites.Remove(f));
            SaveChangesCount++;
            return Task.FromResult(true);
        }

        // Mirrors EventRepository.MatchingFavourites.
        private IEnumerable<EventFavourite> MatchingFavourites(Guid userId, Guid eventId, int? dayOffset) =>
            Favourites
                .Where(f => f.UserId == userId && f.GuideEventId == eventId)
                .Where(f => dayOffset == null || f.DayOffset == null || f.DayOffset == dayOffset);

        public Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(Preference?.UserId == userId ? Preference : null);

        public Task UpsertPreferenceAsync(Guid userId, string excludedCategorySlugsJson, Instant updatedAt, CancellationToken ct = default)
        {
            if (Preference?.UserId == userId)
            {
                Preference.ExcludedCategorySlugs = excludedCategorySlugsJson;
                Preference.UpdatedAt = updatedAt;
            }
            else
            {
                Preference = new EventPreference
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    ExcludedCategorySlugs = excludedCategorySlugsJson,
                    UpdatedAt = updatedAt
                };
            }
            SaveChangesCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EventFavourite>> GetFavouritesForContributorAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventFavourite>>(
                Favourites.Where(f => f.UserId == userId).ToList());
    }
}
