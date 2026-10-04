using Humans.Base.Extensions;
using System.Security.Claims;
using Humans.Camps.Contracts;
using Humans.Events.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using AwesomeAssertions;
using Humans.Base.Csv;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Domain;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Events.Tests.Services;

public sealed class EventServiceTests
{
    private readonly IStringLocalizer<EventsResource> _localizer =
        new StringLocalizer<EventsResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 5, 12, 0));
    private readonly FakeEventRepository _repo = new();
    private readonly ISettingsService _burnSettings = Substitute.For<ISettingsService>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly EventsEmails _emailMessages = new(new StringLocalizer<EventsResource>(new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)), NullLogger<EventsEmails>.Instance);
    private readonly EventService _service;

    public EventServiceTests()
    {
        _service = new EventService(_repo, _burnSettings, _userService, _emailService, _emailMessages, _clock, NullLogger<EventService>.Instance, _localizer);
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
        _burnSettings.GetEventSettingsByIdAsync(eventSettingsId, Arg.Any<CancellationToken>()).Returns(new EventSettingsInfo(
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
    public async Task AddFavouriteAsync_StampsClockAndDayOffset()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _repo.Events.Add(new Event { Id = eventId, Status = EventStatus.Approved, IsRecurring = true, RecurrenceDays = "0,2,4" });

        var added = await _service.AddFavouriteAsync(userId, eventId, dayOffset: 4, TestContext.Current.CancellationToken);

        added.Should().BeTrue();
        _repo.Favourites.Should().ContainSingle(f =>
            f.UserId == userId
            && f.GuideEventId == eventId
            && f.DayOffset == 4
            && f.CreatedAt == _clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task AddFavouriteAsync_AlreadyFavourited_DoesNotWriteAgain()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _repo.Events.Add(new Event { Id = eventId, Status = EventStatus.Approved, IsRecurring = true, RecurrenceDays = "0,2,4" });
        await _service.AddFavouriteAsync(userId, eventId, dayOffset: null, TestContext.Current.CancellationToken);

        var added = await _service.AddFavouriteAsync(userId, eventId, dayOffset: 4, TestContext.Current.CancellationToken);

        added.Should().BeFalse();
        _repo.Favourites.Should().ContainSingle();
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansTheory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task AddFavouriteAsync_RejectsMissingRecurringOccurrences(int day)
    {
        var ev = new Event { Id = Guid.NewGuid(), Status = EventStatus.Approved, IsRecurring = true, RecurrenceDays = "0, 2,4" };
        _repo.Events.Add(ev);

        var act = () => _service.AddFavouriteAsync(Guid.NewGuid(), ev.Id, day, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().Where(e => e.ParamName == "dayOffset");
        _repo.Favourites.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansFact]
    public async Task AddFavouriteAsync_PreservesDayIgnoringBehaviorForNonRecurringEvents()
    {
        var ev = new Event { Id = Guid.NewGuid(), Status = EventStatus.Approved, IsRecurring = false };
        _repo.Events.Add(ev);

        var added = await _service.AddFavouriteAsync(Guid.NewGuid(), ev.Id, 999, TestContext.Current.CancellationToken);

        added.Should().BeTrue();
        _repo.Favourites.Should().ContainSingle().Which.DayOffset.Should().Be(999);
    }

    [HumansFact]
    public async Task AddFavouriteAsync_RejectsMissingEvents()
    {
        var act = () => _service.AddFavouriteAsync(Guid.NewGuid(), Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _repo.Favourites.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansTheory]
    [InlineData(EventStatus.Pending)]
    [InlineData(EventStatus.Rejected)]
    public async Task AddFavouriteAsync_RejectsUnpublishedEvents(EventStatus status)
    {
        var ev = new Event { Id = Guid.NewGuid(), Status = status };
        _repo.Events.Add(ev);
        var act = () => _service.AddFavouriteAsync(Guid.NewGuid(), ev.Id, null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _repo.Favourites.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CategoryPreferences_AcceptedMixedCaseSlugsExcludeEvents(bool previouslyStored)
    {
        var userId = Guid.NewGuid();
        var music = new EventCategory { Id = Guid.NewGuid(), Name = "Music", Slug = "music", IsActive = true };
        var workshop = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.AddRange([music, workshop]);
        var hidden = ExistingEvent(Guid.NewGuid(), music.Id, EventStatus.Approved);
        hidden.Category = music;
        var visible = ExistingEvent(Guid.NewGuid(), workshop.Id, EventStatus.Approved);
        visible.Category = workshop;
        _repo.Events.AddRange([hidden, visible]);
        var registrations = new ServiceCollection();
        registrations.AddKeyedScoped<IEventService>(CachingEventService.InnerServiceKey, (_, _) => _service);
        using var provider = registrations.BuildServiceProvider();
        var cached = new CachingEventService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CachingEventService>.Instance);
        if (previouslyStored)
        {
            _repo.Preference = new EventPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ExcludedCategorySlugs = "[\"MuSiC\"]",
                UpdatedAt = _clock.GetCurrentInstant()
            };
        }
        else
        {
            var controller = new EventsApiController(cached, Substitute.For<ICampServiceRead>(), _userService,
                NullLogger<EventsApiController>.Instance)
            {
                ControllerContext = new()
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
                    }
                }
            };
            var response = await controller.UpdatePreferences(new EventsApiController.UpdatePreferencesRequest
            {
                ExcludedCategorySlugs = ["MuSiC"]
            });
            response.Should().BeOfType<OkObjectResult>();
        }

        var exclusions = await cached.GetExcludedCategorySlugsAsync(userId, TestContext.Current.CancellationToken);
        var events = await cached.GetApprovedEventsAsync(null, null, null, null, exclusions, TestContext.Current.CancellationToken);

        events.Should().ContainSingle().Which.Id.Should().Be(visible.Id);
        exclusions.Should().Equal("music");
        _repo.Preference!.ExcludedCategorySlugs.Should().Be(previouslyStored ? "[\"MuSiC\"]" : "[\"music\"]");
        _repo.SaveChangesCount.Should().Be(previouslyStored ? 0 : 1);
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

    [HumansTheory]
    [InlineData("es", "Hemos recibido el envío de tu evento")]
    [InlineData("", "Your event submission has been received")]
    [InlineData(" ", "Your event submission has been received")]
    [InlineData("not a culture!", "Your event submission has been received")]
    [InlineData("fr-FR", "Your event submission has been received")]
    public async Task SubmitEventAsync_WithActionUrl_EmailsSubmitterConfirmation(string language, string subject)
    {
        using var actorCulture = new CultureScope("fr");
        var submitterId = StubSubmitterWithEmail("sub@example.com", "Burner", language);
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterId,
            Title = "Fire show",
            Status = EventStatus.Pending,
        };

        await _service.SubmitEventAsync(guideEvent, "https://x/Events/MySubmissions", TestContext.Current.CancellationToken);

        _repo.Events.Should().Contain(guideEvent);
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "event_submitted"
                && m.RecipientEmail == "sub@example.com"
                && m.RecipientName == "Burner"
                && m.Subject == subject
                && m.HtmlBody.Contains("Fire show")
                && m.HtmlBody.Contains("https://x/Events/MySubmissions")));
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

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "event_rejected"
                && m.RecipientEmail == "sub@example.com"
                && m.HtmlBody.Contains("Too loud")
                && m.HtmlBody.Contains("https://x/edit")));
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

    [HumansFact]
    public async Task SubmitEventAsync_SubmitterLookupFailure_DoesNotFailCommittedSubmission()
    {
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            Title = "Fire show",
            Status = EventStatus.Pending
        };
        _userService.GetUserInfoAsync(guideEvent.SubmitterUserId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<UserInfo?>(new InvalidOperationException("User lookup unavailable")));

        var act = () => _service.SubmitEventAsync(
            guideEvent, "https://x/Events/MySubmissions", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        _repo.Events.Should().Contain(guideEvent);
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansFact]
    public async Task ApplyModerationAsync_SubmitterLookupFailure_DoesNotFailCommittedDecision()
    {
        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            Title = "Fire show",
            Status = EventStatus.Pending
        };
        _repo.Events.Add(guideEvent);
        _userService.GetUserInfoAsync(guideEvent.SubmitterUserId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<UserInfo?>(new InvalidOperationException("User lookup unavailable")));

        var act = () => _service.ApplyModerationAsync(
            guideEvent.Id, Guid.NewGuid(), EventModerationActionType.Rejected,
            "Too loud", "https://x/edit", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        guideEvent.Status.Should().Be(EventStatus.Rejected);
        _repo.EventModerationActions.Should().ContainSingle(action =>
            action.GuideEventId == guideEvent.Id && action.Action == EventModerationActionType.Rejected);
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    private Guid StubSubmitterWithEmail(string email, string burnerName, string language = "en")
    {
        var userId = Guid.NewGuid();
        // BurnerName mirrors CopyNamesToUser's dual-write from Profile onto User (#1097) —
        // UserInfo.BurnerName reads User.BurnerName only (#1098).
        var user = new User { Id = userId, DisplayName = burnerName, BurnerName = burnerName, PreferredLanguage = language };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            // UserInfoStubHelpers.ToUserInfo lives in Humans.Application.Tests and is not
            // visible across the section boundary; UserInfo.Create is the public builder.
            .Returns(UserInfo.Create(
                user,
                [new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = email, IsVerified = true, IsPrimary = true }],
                [], [], profile: null, []));
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
        slices[0].SectionName.Should().Be(EventService.Events);
        slices[0].Data.Should().NotBeNull();
    }

    [HumansFact]
    public async Task ContributeForUserAsync_IncludesOwnPersonalAndCampSubmissionsAcrossStatuses()
    {
        var userId = Guid.NewGuid();
        var submitted = Instant.FromUtc(2026, 5, 1, 12, 0);
        var personal = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = userId,
            Title = "My draft",
            Description = "Personal description",
            Host = "My host name",
            LocationNote = "Near the fire",
            StartAt = submitted,
            DurationMinutes = 60,
            SubmittedAt = submitted,
            LastUpdatedAt = submitted,
            Status = EventStatus.Draft,
            AdminNotes = "Internal moderator note"
        };
        var camp = new Event
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = userId,
            CampId = Guid.NewGuid(),
            Title = "My camp event",
            Status = EventStatus.Withdrawn,
            StartAt = submitted,
            SubmittedAt = submitted,
            LastUpdatedAt = submitted,
            IsRecurring = true,
            RecurrenceDays = "0,2",
            PriorityRank = 1
        };
        _repo.Events.AddRange([personal, camp,
            new Event { Id = Guid.NewGuid(), SubmitterUserId = Guid.NewGuid(), Title = "Someone else's event" }]);

        var slice = (await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken)).Single();
        var json = System.Text.Json.JsonSerializer.SerializeToElement(slice.Data);
        var events = json.GetProperty("SubmittedEvents").EnumerateArray().ToList();
        events.Should().HaveCount(2);
        var own = events.Single(e => e.GetProperty("Id").GetGuid() == personal.Id);
        own.GetProperty("Title").GetString().Should().Be(personal.Title);
        own.GetProperty("Description").GetString().Should().Be(personal.Description);
        own.GetProperty("Host").GetString().Should().Be(personal.Host);
        own.GetProperty("LocationNote").GetString().Should().Be(personal.LocationNote);
        own.GetProperty("SubmittedAt").GetString().Should().Be("2026-05-01T12:00:00Z");
        own.TryGetProperty("AdminNotes", out _).Should().BeFalse();
        var campExport = events.Single(e => e.GetProperty("Id").GetGuid() == camp.Id);
        campExport.GetProperty("CampId").GetGuid().Should().Be(camp.CampId!.Value);
        campExport.GetProperty("RecurrenceDays").GetString().Should().Be("0,2");
        json.GetProperty("Favourites").GetArrayLength().Should().Be(0);
        json.GetProperty("Preference").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ReturnsEmptySliceWhenUserHasNoData()
    {
        var slices = await _service.ContributeForUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be(EventService.Events);
        slices[0].Data.Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("en", "Title is required.")]
    [InlineData("es", "El campo Title es obligatorio.")]
    [InlineData("de", "Title ist erforderlich.")]
    [InlineData("it", "Il campo Title è obbligatorio.")]
    [InlineData("fr", "Le champ Title est obligatoire.")]
    [InlineData("ca", "El camp Title és obligatori.")]
    public async Task BulkImportAsync_InvalidRow_ReturnsErrorsAndWritesNothing_UsesUploaderCulture(string culture, string expectedError)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var campId = Guid.NewGuid();
            _repo.Categories.Add(new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true });

            var result = await _service.BulkImportAsync(
                campId, Guid.NewGuid(), [Row(title: "")],
                new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

            result.HasErrors.Should().BeTrue();
            result.Errors.Should().ContainSingle(e => e.Errors.Contains(expectedError));
            _repo.Events.Should().BeEmpty();
            _repo.SaveChangesCount.Should().Be(0);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [HumansFact]
    public async Task BulkImportAsync_AmbiguousCategory_ReturnsErrorsAndWritesNothing()
    {
        var campId = Guid.NewGuid();
        _repo.Categories.AddRange(
            new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop-a", IsActive = true },
            new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop-b", IsActive = true });

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row()],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeTrue();
        result.Errors.Should().ContainSingle(error =>
            error.Errors.Contains("Category 'Workshop' matches more than one active category."));
        _repo.Events.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansFact]
    public async Task BulkImportAsync_InvalidRecurrenceDay_ReturnsErrorsAndWritesNothing()
    {
        var campId = Guid.NewGuid();
        _repo.Categories.Add(new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true });

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(isRecurring: true, recurrenceDays: "Mon Funday")],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeTrue();
        result.Errors.Should().ContainSingle(error =>
            error.Errors.Contains("RecurrenceDays must contain only Mon Tue Wed Thu Fri Sat Sun."));
        _repo.Events.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansFact]
    public async Task BulkImportAsync_DuplicateExistingId_ReturnsErrorsAndWritesNothing()
    {
        var campId = Guid.NewGuid();
        var category = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(category);
        var existing = ExistingEvent(campId, category.Id, EventStatus.Approved);
        _repo.Events.Add(existing);

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(),
            [Row(id: existing.Id, title: "First", rowNumber: 2), Row(id: existing.Id, title: "Second", rowNumber: 3)],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeTrue();
        result.Errors.Should().HaveCount(2)
            .And.OnlyContain(error => error.Errors.Contains($"Event {existing.Id} appears more than once in the upload."));
        existing.Title.Should().Be("My Event");
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkImportAsync_CreatesOrEditsColumnsWithoutReplacingExistingIdentity(bool update)
    {
        var campId = Guid.NewGuid();
        var submitter = Guid.NewGuid();
        var cat = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(cat);
        var existing = ExistingEvent(campId, Guid.NewGuid(), EventStatus.Approved);
        var existingSubmitter = existing.SubmitterUserId;
        if (update) _repo.Events.Add(existing);

        var result = await _service.BulkImportAsync(
            campId, submitter, [Row(id: update ? existing.Id : null, title: "Fire workshop",
                description: "Bring gloves", startTime: "14:00", duration: 90, location: "North tent",
                host: "Spark", isRecurring: true, recurrenceDays: "Wed Fri", priority: 2)],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeFalse();
        result.CreatedCount.Should().Be(update ? 0 : 1);
        result.UpdatedCount.Should().Be(update ? 1 : 0);
        var persisted = _repo.Events.Should().ContainSingle().Subject;
        persisted.Status.Should().Be(EventStatus.Pending);
        persisted.CampId.Should().Be(campId);
        persisted.SubmitterUserId.Should().Be(update ? existingSubmitter : submitter);
        if (update) persisted.Id.Should().Be(existing.Id);
        persisted.CategoryId.Should().Be(cat.Id);
        persisted.Title.Should().Be("Fire workshop");
        persisted.Description.Should().Be("Bring gloves");
        persisted.StartAt.Should().Be(Instant.FromUtc(2026, 7, 8, 14, 0));
        persisted.DurationMinutes.Should().Be(90);
        persisted.LocationNote.Should().Be("North tent");
        persisted.Host.Should().Be("Spark");
        persisted.IsRecurring.Should().BeTrue();
        persisted.RecurrenceDays.Should().Be("0,2");
        persisted.PriorityRank.Should().Be(2);
    }

    [HumansFact]
    public async Task BulkImportAsync_UnrankedRow_IsAcceptedAndStaysUnranked()
    {
        var campId = Guid.NewGuid();
        _repo.Categories.Add(new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true });

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(priority: null)],
            new LocalDate(2026, 7, 8), 6, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeFalse();
        _repo.Events.Should().ContainSingle().Which.PriorityRank.Should().BeNull();
    }

    [HumansFact]
    public async Task BulkTemplate_UnrankedEvent_RoundTripsAsUnranked()
    {
        var campId = Guid.NewGuid();
        var category = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(category);
        var existing = ExistingEvent(campId, category.Id, EventStatus.Approved);
        existing.Category = category;
        existing.PriorityRank = null;
        _repo.Events.Add(existing);

        var bytes = await _service.BuildBulkUploadTemplateAsync(campId, "Fire Barrio", TestContext.Current.CancellationToken);
        var rows = BulkEventCsvParser.Parse(HumansCsv.Utf8WithBom.GetString(bytes).TrimStart('\uFEFF'), _localizer);

        rows.Should().ContainSingle().Which.PriorityRank.Should().BeNull();
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

    [HumansTheory]
    [InlineData("Wed", "0")]
    [InlineData("Thu", "1,8")]
    public async Task BulkImportAsync_TitleEditPreservesRecurrenceUnlessWeekdaysChange(string days, string expectedOffsets)
    {
        var gate = new LocalDate(2026, 7, 8);
        var campId = Guid.NewGuid();
        var category = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(category);
        var existing = ExistingEvent(campId, category.Id, EventStatus.Approved);
        existing.IsRecurring = true;
        existing.RecurrenceDays = "0";
        _repo.Events.Add(existing);

        var result = await _service.BulkImportAsync(
            campId, Guid.NewGuid(), [Row(id: existing.Id, title: "Renamed", isRecurring: true, recurrenceDays: days)],
            gate, 8, DateTimeZone.Utc, TestContext.Current.CancellationToken);

        result.HasErrors.Should().BeFalse();
        result.UpdatedCount.Should().Be(1);
        existing.Title.Should().Be("Renamed");
        existing.RecurrenceDays.Should().Be(expectedOffsets);
        existing.Status.Should().Be(EventStatus.Pending);
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

    [HumansFact]
    public async Task GetCampSubmissionsSummaryAsync_BucketsByStatusAndIncludesOnlyCampEvents()
    {
        var campId = Guid.NewGuid();
        var older = new Event
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Title = "Older",
            Status = EventStatus.Approved,
            SubmittedAt = Instant.FromUtc(2026, 5, 1, 12, 0)
        };
        var newer = new Event
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Title = "Newer",
            Status = EventStatus.Pending,
            SubmittedAt = Instant.FromUtc(2026, 5, 3, 12, 0)
        };
        var otherCamp = new Event
        {
            Id = Guid.NewGuid(),
            CampId = Guid.NewGuid(),
            Title = "Other camp",
            Status = EventStatus.Approved,
            SubmittedAt = Instant.FromUtc(2026, 5, 2, 12, 0)
        };
        _repo.Events.AddRange([older, newer, otherCamp]);

        var summary = await _service.GetCampSubmissionsSummaryAsync(campId, TestContext.Current.CancellationToken);

        summary.SubmittedCount.Should().Be(2);
        summary.ApprovedCount.Should().Be(1);
        summary.PendingCount.Should().Be(1);
        summary.Events.Select(e => e.Title).Should().BeEquivalentTo(["Newer", "Older"]);
    }

    [HumansFact]
    public async Task BuildBulkUploadTemplateAsync_IncludesExistingNonWithdrawnEventsAndBanner()
    {
        var campId = Guid.NewGuid();
        var category = new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true };
        _repo.Categories.Add(category);
        var startAt = (new LocalDate(2026, 7, 8) + new LocalTime(9, 30)).InZoneLeniently(DateTimeZone.Utc).ToInstant();
        var kept = new Event
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            CategoryId = category.Id,
            Category = category,
            Title = "Fire Circle",
            Description = "Desc",
            StartAt = startAt,
            DurationMinutes = 60,
            PriorityRank = 1,
            Status = EventStatus.Approved,
            SubmittedAt = _clock.GetCurrentInstant()
        };
        var withdrawn = new Event
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            CategoryId = category.Id,
            Category = category,
            Title = "Cancelled Talk",
            Status = EventStatus.Withdrawn,
            SubmittedAt = _clock.GetCurrentInstant()
        };
        _repo.Events.AddRange([kept, withdrawn]);

        var bytes = await _service.BuildBulkUploadTemplateAsync(campId, "Fire Barrio", TestContext.Current.CancellationToken);

        var csv = HumansCsv.Utf8WithBom.GetString(bytes);
        csv.Should().Contain("ELSEWHERE EVENT GUIDE");
        csv.Should().Contain("Fire Circle");
        csv.Should().Contain("Fire Barrio");
        csv.Should().NotContain("Cancelled Talk");
    }

    [HumansFact]
    public async Task BuildBulkUploadTemplateAsync_NoEvents_WritesExampleRow()
    {
        var campId = Guid.NewGuid();
        _repo.Categories.Add(new EventCategory { Id = Guid.NewGuid(), Name = "Workshop", Slug = "workshop", IsActive = true });

        var bytes = await _service.BuildBulkUploadTemplateAsync(campId, "Empty Barrio", TestContext.Current.CancellationToken);

        var csv = HumansCsv.Utf8WithBom.GetString(bytes);
        csv.Should().Contain("Example Event");
    }

    private static BulkCsvRow Row(
        Guid? id = null, string title = "My Event", string description = "Desc",
        string category = "Workshop", string date = "2026-07-08", string startTime = "09:30",
        int duration = 60, string? location = null, string? host = null,
        bool isRecurring = false, string? recurrenceDays = null, int? priority = 1, int rowNumber = 2)
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

    [HumansFact]
    public async Task EraseForUserAsync_ClearsTheirHostButKeepsTheSubmission()
    {
        var userId = Guid.NewGuid();
        var submission = ExistingEvent(Guid.NewGuid(), Guid.NewGuid(), EventStatus.Approved);
        submission.SubmitterUserId = userId;
        submission.Host = "Frank";
        var someoneElses = ExistingEvent(Guid.NewGuid(), Guid.NewGuid(), EventStatus.Approved);
        someoneElses.Host = "Nurse";
        _repo.Events.Add(submission);
        _repo.Events.Add(someoneElses);
        _repo.Favourites.Add(new EventFavourite
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GuideEventId = submission.Id,
            CreatedAt = _clock.GetCurrentInstant(),
        });

        await _service.EraseForUserAsync(userId, TestContext.Current.CancellationToken);

        // The listing survives — event_moderation_actions references it under a Restrict FK and
        // other people favourited it — but the name they ran it under does not.
        _repo.Events.Should().Contain(submission);
        submission.Host.Should().BeNull();
        someoneElses.Host.Should().Be("Nurse");
        _repo.Favourites.Should().BeEmpty();
    }

    [HumansFact]
    public void ErasureDeclaration_StatesWhatSurvivesRatherThanClaimingFullErasure()
    {
        _service.ErasureDeclaration[EventService.Events]
            .Should().NotBeNull().And.Contain("Host");
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

        public Task<int> DeleteFavouritesAndPreferenceForUserAsync(Guid userId, CancellationToken ct = default)
        {
            var removed = Favourites.RemoveAll(f => f.UserId == userId);
            if (Preference?.UserId == userId)
            {
                Preference = null;
                removed++;
            }
            return Task.FromResult(removed);
        }

        public Task<int> ClearSubmitterHostForUserAsync(Guid userId, CancellationToken ct = default)
        {
            var cleared = 0;
            foreach (var submission in Events.Where(e => e.SubmitterUserId == userId && e.Host is not null))
            {
                submission.Host = null;
                cleared++;
            }
            return Task.FromResult(cleared);
        }
    }
}
