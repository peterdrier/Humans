using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Humans.Base;
using Humans.Base.Extensions;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Base.Constants;
using Humans.Events.Contracts;
using Humans.Events.Controllers;
using Humans.Events.Domain;
using Humans.Events.Models;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;

namespace Humans.Events.Tests.Controllers;

/// <summary>
/// Authorization coverage for the individual-event edit route
/// (<c>GET/POST /Events/Submit/{eventId}/Edit</c>). The "Edit event" link in
/// moderation lifecycle emails points here. It must serve the submitter and
/// Events admins, return 403 (not 404) for any other signed-in user, and 404
/// only when the event genuinely doesn't exist on this route.
/// </summary>
public class EventsControllerTests
{
    private readonly ILogger<EventsController> _logger = Substitute.For<ILogger<EventsController>>();
    private readonly IEventService _guide = Substitute.For<IEventService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly IAuthorizationService _authz = Substitute.For<IAuthorizationService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<EventsResource> _localizer = Substitute.For<IStringLocalizer<EventsResource>>();

    [HumansTheory]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Guide")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Burn")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Submissions")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "CampSettings")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Camps")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "CampSummary")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Categories")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Venues")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Event")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Categories")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Venues")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Favourites")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Camps")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Excluded")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Favourites")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Approved")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Camps")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Submitter")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Categories")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Venues")]
    public async Task MemberReadPages_StopLoadingAfterRequestCancellation(string route, string boundary)
    {
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var controller = BuildController(userId);
        controller.HttpContext.RequestAborted = request.Token;
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        var abandon = false;
        async Task<T> Read<T>(T value, string current, CancellationToken ct)
        {
            if (abandon && string.Equals(current, boundary, StringComparison.Ordinal))
                await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return value;
        }
        StubEditableGuideSettings();
        var settings = (await _guide.GetGuideSettingsAsync(Xunit.TestContext.Current.CancellationToken))! with
        {
            SubmissionOpenAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            SubmissionCloseAt = Instant.FromUtc(2026, 6, 1, 0, 0),
        };
        _clock.GetCurrentInstant().Returns(Instant.FromUtc(2026, 5, 15, 0, 0));
        var burn = MakeBurnSettings() with { Id = settings.EventSettingsId };
        var guideEvent = MakeEvent(null, userId, EventStatus.ResubmitRequested);
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(), campId, "camp", 2026, null, "Camp", "", "en", [],
            default, default, default, default, 1, null, null, null, 0, null, null)
        { LeadUserIds = [userId] };
        var camp = new CampInfo(campId, "camp", "camp@example.org", "", false, 0, [season]);
        var submitterId = Guid.NewGuid();
        var approved = new ApprovedEventView(Guid.NewGuid(), null, null, submitterId, Guid.NewGuid(), "music", "Music", false,
            null, "Event", "Description", null, null, Instant.FromUtc(2026, 8, 1, 18, 0), 60, false, null, null,
            Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 1, 0, 0));
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<UserInfo?>(Read<UserInfo?>(MakeUserInfo(call.Arg<Guid>(), "Human"),
                call.Arg<Guid>() == userId ? "Viewer" : "Submitter", call.Arg<CancellationToken>())));
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
            Read<EventGuideSettingsView?>(settings, "Guide", call.Arg<CancellationToken>()));
        _guide.GetEventSettingsByIdAsync(settings.EventSettingsId, Arg.Any<CancellationToken>()).Returns(call =>
            Read<EventSettingsInfo?>(burn, "Burn", call.Arg<CancellationToken>()));
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(call =>
            Read<Event?>(guideEvent, "Event", call.Arg<CancellationToken>()));
        _guide.GetUserSubmissionsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            Read<IReadOnlyList<EventInfo>>([], "Submissions", call.Arg<CancellationToken>()));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
            Read(new CampSettingsInfo(2026, [2026]), "CampSettings", call.Arg<CancellationToken>()));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(call =>
            Read<IReadOnlyList<CampInfo>>([camp], "Camps", call.Arg<CancellationToken>()));
        _guide.GetCampSubmissionsSummaryAsync(campId, Arg.Any<CancellationToken>()).Returns(call =>
            Read(new CampSubmissionsSummary(0, 0, 0, []), "CampSummary", call.Arg<CancellationToken>()));
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns(call =>
            Read<IReadOnlyList<EventCategoryView>>([], "Categories", call.Arg<CancellationToken>()));
        _guide.GetActiveVenuesAsync(Arg.Any<CancellationToken>()).Returns(call =>
            Read<IReadOnlyList<EventVenueView>>([], "Venues", call.Arg<CancellationToken>()));
        _guide.GetFavouritesWithEventsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            Read<IReadOnlyList<EventFavouriteInfo>>([], "Favourites", call.Arg<CancellationToken>()));
        _guide.GetExcludedCategorySlugsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            Read<List<string>>([], "Excluded", call.Arg<CancellationToken>()));
        _guide.GetApprovedEventsAsync(null, null, null, null, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
            Read<IReadOnlyList<ApprovedEventView>>([approved], "Approved", call.Arg<CancellationToken>()));
        Func<Task<IActionResult>> load = route switch
        {
            nameof(EventsController.MySubmissions) => controller.MySubmissions,
            nameof(EventsController.Submit) => controller.Submit,
            nameof(EventsController.Edit) => () => controller.Edit(guideEvent.Id),
            nameof(EventsController.Schedule) => controller.Schedule,
            _ => () => controller.Browse(null, null, null, null),
        };
        (await load()).Should().BeOfType<ViewResult>();
        abandon = true;
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    public void SubmissionForms_LocalizeValidationMessages(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection();
        registrations.AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var cases = new (object Model, string Field, string Key, object[] Arguments)[]
        {
            (new IndividualEventFormViewModel(), "Title", "Validation_Required", ["Title"]),
            (new IndividualEventFormViewModel(), "Description", "Validation_Required", ["Description"]),
            (new IndividualEventFormViewModel { Title = new string('x', 81) }, "Title", "Validation_MaxLength", ["Title", 80]),
            (new IndividualEventFormViewModel { Description = new string('x', 451) }, "Description", "Validation_MaxLength", ["Description", 450]),
            (new IndividualEventFormViewModel { LocationNote = new string('x', 121) }, "LocationNote", "Validation_MaxLength", ["Location Note", 120]),
            (new IndividualEventFormViewModel { Host = new string('x', 41) }, "Host", "Validation_MaxLength", ["Host", 40]),
            (new IndividualEventFormViewModel { DurationMinutes = 14 }, "DurationMinutes", "Validation_Range", ["Duration (minutes)", 15, 1440]),
            (new CampEventFormViewModel(), "Title", "Validation_Required", ["Title"]),
            (new CampEventFormViewModel(), "Description", "Validation_Required", ["Description"]),
            (new CampEventFormViewModel(), "PriorityRank", "Validation_Required", ["Priority Rank"]),
            (new CampEventFormViewModel { Title = new string('x', 81) }, "Title", "Validation_MaxLength", ["Title", 80]),
            (new CampEventFormViewModel { Description = new string('x', 451) }, "Description", "Validation_MaxLength", ["Description", 450]),
            (new CampEventFormViewModel { LocationNote = new string('x', 121) }, "LocationNote", "Validation_MaxLength", ["Location Note", 120]),
            (new CampEventFormViewModel { Host = new string('x', 41) }, "Host", "Validation_MaxLength", ["Host", 40]),
            (new CampEventFormViewModel { DurationMinutes = 481 }, "DurationMinutes", "Validation_Range", ["Duration (minutes)", 15, 480]),
            (new CampEventFormViewModel { PriorityRank = 101 }, "PriorityRank", "Validation_Range", ["Priority Rank", 1, 100]),
        };
        foreach (var (model, field, key, arguments) in cases)
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            validator.Validate(context, null, "", model);
            var expected = localizer[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            context.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
        }
    }

    [HumansFact]
    public async Task Edit_NonSubmitterNonAdmin_ReturnsForbid()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: null, EventStatus.ResubmitRequested);
        var controller = BuildController(Guid.NewGuid());

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Edit_AdminNonSubmitter_ReturnsEditForm()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: null, EventStatus.ResubmitRequested);
        StubEditableGuideSettings();
        var controller = BuildController(Guid.NewGuid(), RoleNames.Admin);

        var result = await controller.Edit(eventId);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("IndividualEventForm");
    }

    [HumansFact]
    public async Task Edit_Submitter_ReturnsEditForm()
    {
        var submitterId = Guid.NewGuid();
        var eventId = StubEvent(submitterId, campId: null, EventStatus.ResubmitRequested);
        StubEditableGuideSettings();
        var controller = BuildController(submitterId);

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task Edit_MissingEvent_ReturnsNotFound()
    {
        var eventId = Guid.NewGuid();
        _guide.GetEventForModerationAsync(eventId, Arg.Any<CancellationToken>())
            .Returns((Event?)null);
        var controller = BuildController(Guid.NewGuid(), RoleNames.Admin);

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Edit_CampEventOnIndividualRoute_ReturnsNotFound()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: Guid.NewGuid(), EventStatus.ResubmitRequested);
        var controller = BuildController(Guid.NewGuid(), RoleNames.Admin);

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Update_NonSubmitterNonAdmin_ReturnsForbid()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: null, EventStatus.ResubmitRequested);
        var controller = BuildController(Guid.NewGuid());

        var result = await controller.Update(eventId, new IndividualEventFormViewModel());

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task MySubmissions_OrdersCampEventsMostRecentlySubmittedFirst()
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(), campId, "camp", 2026, null, "Camp", "", "en", [],
            default, default, default, default, 1, null, null, null, 0, null, null)
        {
            LeadUserIds = [userId]
        };
        var camp = new CampInfo(campId, "camp", "camp@example.org", "", false, 0, [season]);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026]));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([camp]);
        StubEditableGuideSettings();
        var settings = await _guide.GetGuideSettingsAsync();
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings! with
        {
            SubmissionOpenAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            SubmissionCloseAt = Instant.FromUtc(2026, 6, 1, 0, 0)
        });
        _guide.GetUserSubmissionsAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
        var older = new EventInfo(
            Guid.NewGuid(), campId, null, userId, Guid.NewGuid(), "Music", "music", false,
            null, "Older", "", null, null, Instant.FromUtc(2026, 8, 1, 18, 0), 60, false,
            null, null, EventStatus.Pending, Instant.FromUtc(2026, 5, 1, 12, 0),
            Instant.FromUtc(2026, 5, 1, 12, 0), []);
        var newer = older with { Id = Guid.NewGuid(), Title = "Newer", SubmittedAt = older.SubmittedAt + Duration.FromDays(1) };
        _guide.GetCampSubmissionsSummaryAsync(campId, Arg.Any<CancellationToken>())
            .Returns(new CampSubmissionsSummary(2, 0, 2, [older, newer]));
        var controller = BuildController(userId);
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());

        var result = await controller.MySubmissions();

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<MySubmissionsViewModel>().Subject;
        model.Barrios.Should().ContainSingle().Which.Events.Select(e => e.Title)
            .Should().Equal("Newer", "Older");
    }

    private Guid StubEvent(Guid submitterId, Guid? campId, EventStatus status)
    {
        var guideEvent = MakeEvent(campId, submitterId, status);
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>())
            .Returns(guideEvent);
        return guideEvent.Id;
    }

    [HumansTheory]
    [Xunit.InlineData("format")]
    [Xunit.InlineData("quote")]
    [Xunit.InlineData("io")]
    [Xunit.InlineData("caller-cancel")]
    [Xunit.InlineData("dependency-cancel")]
    public async Task BulkUpload_SeparatesInputErrorsFromUnexpectedFailures(string failureKind)
    {
        var userId = Guid.NewGuid();
        var camp = new CampInfo(Guid.NewGuid(), "camp", "camp@example.org", "", false, 0, []);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authz.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        StubEditableGuideSettings();
        _localizer[Arg.Any<string>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>()));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>()));
        var controller = BuildController(userId);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        controller.ControllerContext.ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(EventsController.BulkUploadImport) };
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        var file = Substitute.For<IFormFile>();
        file.Length.Returns(100);
        using var cancellation = new CancellationTokenSource();
        Exception? failure = null;
        if (string.Equals(failureKind, "caller-cancel", StringComparison.Ordinal))
        {
            await cancellation.CancelAsync();
            controller.HttpContext.RequestAborted = cancellation.Token;
            failure = new OperationCanceledException(cancellation.Token);
        }
        else if (string.Equals(failureKind, "dependency-cancel", StringComparison.Ordinal))
            failure = new OperationCanceledException("Upload storage cancelled");
        else if (string.Equals(failureKind, "io", StringComparison.Ordinal))
            failure = new IOException("Upload stream unavailable");
        if (failure is not null) file.OpenReadStream().Returns<Stream>(_ => throw failure);
        else
        {
            const string header = "Id,Barrio,Status,Title,Description,Category,Date,StartTime,DurationMinutes,LocationNote,Host,IsRecurring,RecurrenceDays,PriorityRank";
            var csv = string.Equals(failureKind, "quote", StringComparison.Ordinal)
                ? header + "\n,Camp,,Bad\"quote,Description,Workshop,2026-08-01,09:30,60,,,false,,1\n"
                : "MissingRequiredColumns\nrow";
            file.OpenReadStream().Returns(_ => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)));
        }
        var act = () => controller.BulkUploadImport(camp.Slug, file);
        if (string.Equals(failureKind, "caller-cancel", StringComparison.Ordinal))
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
            _logger.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            (await act()).Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(EventsController.MySubmissions));
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(string.Equals(failureKind, "format", StringComparison.Ordinal)
                ? "Events_Upload_ParseErrorDetail" : "Events_Upload_ParseFailed");
            var args = _logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
            args[0].Should().Be(failure is null ? LogLevel.Warning : LogLevel.Error);
            args[3].Should().BeSameAs(failure);
            args[2]!.ToString().Should().Contain(camp.Slug);
        }
        _guide.ReceivedCalls().Should().NotContain(c => string.Equals(c.GetMethodInfo().Name, nameof(IEventService.BulkImportAsync), StringComparison.Ordinal));
    }

    private void StubEditableGuideSettings()
    {
        var settingsId = Guid.NewGuid();
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new EventGuideSettingsView(
                Id: Guid.NewGuid(),
                EventSettingsId: settingsId,
                SubmissionOpenAt: Instant.MinValue,
                SubmissionCloseAt: Instant.MaxValue,
                GuidePublishAt: Instant.MaxValue,
                MaxPrintSlots: 100,
                TimeZoneId: "Europe/Madrid",
                CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0)));
        _guide.GetEventSettingsByIdAsync(settingsId, Arg.Any<CancellationToken>())
            .Returns(MakeBurnSettings());
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _guide.GetActiveVenuesAsync(Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private EventsController BuildController(Guid currentUserId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, currentUserId.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        _users.GetUserInfoAsync(currentUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(currentUserId, "Current User")));

        return new EventsController(_guide, _users, _camps, _authz, _clock, _logger, _localizer)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static EventSettingsInfo MakeBurnSettings() => new(
        Id: Guid.NewGuid(),
        EventName: "Test Burn",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 8, 1),
        BuildStartOffset: 0,
        EventEndOffset: 2,
        StrikeEndOffset: 0,
        FirstCrewStartOffset: 0,
        SetupWeekStartOffset: 0,
        PreEventWeekStartOffset: 0,
        FinishingWeekendStartOffset: 0,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null);

    private static Event MakeEvent(Guid? campId, Guid submitterId, EventStatus status) => new()
    {
        Id = Guid.NewGuid(),
        CampId = campId,
        GuideSharedVenueId = campId == null ? Guid.NewGuid() : null,
        SubmitterUserId = submitterId,
        CategoryId = Guid.NewGuid(),
        Title = "Test Event",
        Description = "Description",
        StartAt = Instant.FromUtc(2026, 8, 1, 18, 0),
        DurationMinutes = 60,
        Status = status,
        Category = new EventCategory { Id = Guid.NewGuid(), Name = "Music", Slug = "music", IsSensitive = false },
    };

    private static UserInfo MakeUserInfo(Guid userId, string burnerName)
    {
        var profile = UserFixtures.Profile(
            burnerName: burnerName,
            firstName: "Test",
            lastName: "User",
            isApproved: true);
        var user = new User
        {
            Id = userId,
            DisplayName = "Test User",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        return UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            communicationPreferences: []);
    }
}
