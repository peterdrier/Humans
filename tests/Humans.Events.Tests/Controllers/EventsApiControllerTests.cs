using System.Security.Claims;
using System.Reflection;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Controllers;
using Humans.Events.Models;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Events.Tests.Controllers;

/// <summary>
/// Host-attribution coverage for <see cref="EventsApiController"/> (US-26.3 / US-26.6).
/// The published-guide host falls back to the submitter's burner name for individual
/// events when no explicit host is set; camp events show their own host as-is and never
/// fall back to a submitter.
/// </summary>
public class EventsApiControllerTests
{
    private readonly IEventManagement _guide = Substitute.For<IEventManagement>();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    public EventsApiControllerTests()
    {
        // No guide settings → no event-settings/timezone/gate-date lookups in the action.
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventGuideSettingsView?)null);
    }

    public enum EventsRoute
    {
        Events,
        Event,
        Barrios,
        Barrio,
        Categories,
        Preferences,
        UpdatePreferences,
        Favourites
    }

    public enum ReadBoundary
    {
        Guide,
        Burn,
        Excluded,
        Approved,
        Event,
        Camps,
        Submitter,
        Categories,
        Favourites
    }

    public enum MemberAction
    {
        GetPreferences,
        UpdatePreferences,
        GetFavourites,
        AddFavourite,
        RemoveFavourite,
        GetEvents,
        DownloadCsv
    }

    [HumansTheory]
    [InlineData(EventsRoute.Events, ReadBoundary.Guide)]
    [InlineData(EventsRoute.Events, ReadBoundary.Burn)]
    [InlineData(EventsRoute.Events, ReadBoundary.Excluded)]
    [InlineData(EventsRoute.Events, ReadBoundary.Approved)]
    [InlineData(EventsRoute.Events, ReadBoundary.Camps)]
    [InlineData(EventsRoute.Events, ReadBoundary.Submitter)]
    [InlineData(EventsRoute.Event, ReadBoundary.Guide)]
    [InlineData(EventsRoute.Event, ReadBoundary.Event)]
    [InlineData(EventsRoute.Event, ReadBoundary.Submitter)]
    [InlineData(EventsRoute.Barrios, ReadBoundary.Guide)]
    [InlineData(EventsRoute.Barrios, ReadBoundary.Approved)]
    [InlineData(EventsRoute.Barrio, ReadBoundary.Guide)]
    [InlineData(EventsRoute.Barrio, ReadBoundary.Approved)]
    [InlineData(EventsRoute.Categories, ReadBoundary.Categories)]
    [InlineData(EventsRoute.Preferences, ReadBoundary.Excluded)]
    [InlineData(EventsRoute.UpdatePreferences, ReadBoundary.Categories)]
    [InlineData(EventsRoute.Favourites, ReadBoundary.Guide)]
    [InlineData(EventsRoute.Favourites, ReadBoundary.Favourites)]
    [InlineData(EventsRoute.Favourites, ReadBoundary.Camps)]
    public async Task ReadPreflight_AbandonedRequest_CancelsAtLookup(EventsRoute route, ReadBoundary boundary)
    {
        using var request = new CancellationTokenSource();
        async Task<T> ReadAsync<T>(T value, ReadBoundary current, CancellationToken ct)
        {
            if (current == boundary)
                await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return value;
        }
        var controller = BuildController();
        controller.HttpContext.RequestAborted = request.Token;
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], null));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var burn = BurnFixtures.Burn(year: 2026);
        var settings = new EventGuideSettingsView(Guid.NewGuid(), burn.Id, Instant.MinValue,
            Instant.MaxValue, Instant.MaxValue, 100, "Europe/Madrid", Instant.MinValue, Instant.MinValue);
        var approved = MakeEvent(null, Guid.NewGuid(), null);
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventGuideSettingsView?>(settings, ReadBoundary.Guide, call.Arg<CancellationToken>()));
        _guide.GetEventSettingsByIdAsync(burn.Id, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventSettingsInfo?>(burn, ReadBoundary.Burn, call.Arg<CancellationToken>()));
        _guide.GetExcludedCategorySlugsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<List<string>>([], ReadBoundary.Excluded, call.Arg<CancellationToken>()));
        _guide.GetApprovedEventsAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<ApprovedEventView>>([approved], ReadBoundary.Approved, call.Arg<CancellationToken>()));
        _guide.GetApprovedEventByIdAsync(approved.Id, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<ApprovedEventView?>(approved, ReadBoundary.Event, call.Arg<CancellationToken>()));
        _camps.GetCampsForEventAsync(burn.Id, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<CampInfo>>([], ReadBoundary.Camps, call.Arg<CancellationToken>()));
        _users.GetUserInfoAsync(approved.SubmitterUserId, Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<UserInfo?>(ReadAsync<UserInfo?>(MakeUserInfo(approved.SubmitterUserId, "Host"),
                ReadBoundary.Submitter, call.Arg<CancellationToken>())));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(ReadAsync<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [approved.SubmitterUserId] = MakeUserInfo(approved.SubmitterUserId, "Host") },
                ReadBoundary.Submitter, call.Arg<CancellationToken>())));
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventCategoryView>>([], ReadBoundary.Categories, call.Arg<CancellationToken>()));
        _guide.GetFavouritesWithEventsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventFavouriteInfo>>([], ReadBoundary.Favourites, call.Arg<CancellationToken>()));
        Func<Task<IActionResult>> act = route switch
        {
            EventsRoute.Events => () => controller.GetEvents(null, null, null, null),
            EventsRoute.Event => () => controller.GetEvent(approved.Id),
            EventsRoute.Barrios => controller.GetBarrios,
            EventsRoute.Barrio => () => controller.GetBarrio(Guid.NewGuid()),
            EventsRoute.Categories => controller.GetCategories,
            EventsRoute.Preferences => controller.GetPreferences,
            EventsRoute.UpdatePreferences => () => controller.UpdatePreferences(new EventsApiController.UpdatePreferencesRequest()),
            EventsRoute.Favourites => controller.GetFavourites,
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _guide.DidNotReceive().SavePreferenceAsync(
            Arg.Any<Guid>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetEvents_IndividualEventWithoutHost_FallsBackToSubmitterBurnerName()
    {
        var submitterId = Guid.NewGuid();
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [submitterId] = MakeUserInfo(submitterId, "Fire Dancer") });
        StubApprovedEvents(MakeEvent(campId: null, submitterId, host: null));

        var dto = await SingleResultAsync();

        dto.Host.Should().Be("Fire Dancer");
    }

    [HumansFact]
    public async Task GetEvents_IndividualEventWithHost_UsesHost()
    {
        var submitterId = Guid.NewGuid();
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [submitterId] = MakeUserInfo(submitterId, "Fire Dancer") });
        StubApprovedEvents(MakeEvent(campId: null, submitterId, host: "Explicit Host"));

        var dto = await SingleResultAsync();

        dto.Host.Should().Be("Explicit Host");
    }

    [HumansFact]
    public async Task GetEvents_CampEventWithoutHost_HostIsNull()
    {
        StubApprovedEvents(MakeEvent(campId: Guid.NewGuid(), Guid.NewGuid(), host: null));

        var dto = await SingleResultAsync();

        dto.Host.Should().BeNull();
    }

    [HumansFact]
    public async Task GetEvents_CampEventWithHost_UsesHost()
    {
        StubApprovedEvents(MakeEvent(campId: Guid.NewGuid(), Guid.NewGuid(), host: "Camp Host"));

        var dto = await SingleResultAsync();

        dto.Host.Should().Be("Camp Host");
    }

    [HumansFact]
    public void FavouriteMutations_RequireAntiforgeryValidation()
    {
        var actions = new[]
        {
            nameof(EventsApiController.AddFavourite),
            nameof(EventsApiController.RemoveFavourite)
        };

        foreach (var action in actions)
        {
            var method = typeof(EventsApiController).GetMethod(action);
            method.Should().NotBeNull();
            method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        }
    }

    [HumansTheory]
    [InlineData("Events")]
    [InlineData("Barrios")]
    [InlineData("Barrio")]
    public async Task ApprovedLists_OrderByEventStart(string route)
    {
        var earlyCamp = Guid.NewGuid();
        var lateCamp = string.Equals(route, "Barrio", StringComparison.Ordinal) ? earlyCamp : Guid.NewGuid();
        var early = MakeEvent(earlyCamp, Guid.NewGuid(), null) with
        { StartAt = Instant.FromUtc(2026, 8, 1, 10, 0) };
        var late = MakeEvent(lateCamp, Guid.NewGuid(), null);
        StubApprovedEvents(late, early);
        var controller = BuildController();

        var result = route switch
        {
            "Events" => await controller.GetEvents(null, null, null, null),
            "Barrios" => await controller.GetBarrios(),
            "Barrio" => await controller.GetBarrio(earlyCamp),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

        var payload = result.Should().BeOfType<OkObjectResult>().Which.Value;
        if (string.Equals(route, "Barrios", StringComparison.Ordinal))
            payload.Should().BeAssignableTo<IEnumerable<GuideCampApiDto>>().Which
                .Select(c => c.Id).Should().Equal(earlyCamp, lateCamp);
        else
        {
            var events = string.Equals(route, "Barrio", StringComparison.Ordinal)
                ? payload.Should().BeOfType<GuideCampDetailApiDto>().Which.Events
                : payload.Should().BeAssignableTo<IEnumerable<GuideEventApiDto>>().Which;
            events.Select(e => e.Id).Should().Equal(early.Id, late.Id);
        }
    }

    private void StubApprovedEvents(params ApprovedEventView[] events) =>
        _guide.GetApprovedEventsAsync(
                Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(events);

    private async Task<GuideEventApiDto> SingleResultAsync()
    {
        var controller = BuildController();
        var result = await controller.GetEvents(day: null, categorySlug: null, barrioId: null, q: null);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IEnumerable<GuideEventApiDto>>().Subject;
        return list.Should().ContainSingle().Subject;
    }

    [HumansFact]
    public async Task AddFavourite_ReturnsOutcomeResponse()
    {
        var controller = BuildController();
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], null));
        var eventId = Guid.NewGuid();
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var responses = new Dictionary<AddFavouriteOutcome, Type>
        {
            [AddFavouriteOutcome.EventUnavailable] = typeof(NotFoundResult),
            [AddFavouriteOutcome.InvalidDay] = typeof(BadRequestResult),
            [AddFavouriteOutcome.AlreadyExists] = typeof(ConflictObjectResult),
            [AddFavouriteOutcome.Added] = typeof(OkObjectResult),
        };
        foreach (var (outcome, responseType) in responses)
        {
            _guide.AddFavouriteAsync(userId, eventId, 999, Arg.Any<CancellationToken>()).Returns(outcome);

            var result = await controller.AddFavourite(eventId, 999);

            result.Should().BeOfType(responseType);
        }
    }

    public enum DependencyFailure
    {
        MissingKey,
        InvalidArgument
    }

    [HumansTheory]
    [InlineData(DependencyFailure.MissingKey)]
    [InlineData(DependencyFailure.InvalidArgument)]
    public async Task AddFavourite_PropagatesDependencyFailures(DependencyFailure failureKind)
    {
        var controller = BuildController();
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], null));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var eventId = Guid.NewGuid();
        Exception failure = failureKind == DependencyFailure.MissingKey
            ? new KeyNotFoundException("Dependency failure")
            : new ArgumentOutOfRangeException(nameof(failureKind));
        _guide.AddFavouriteAsync(userId, eventId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AddFavouriteOutcome>(failure));

        var act = () => controller.AddFavourite(eventId, null);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
    }

    [HumansFact]
    public async Task GetCategories_OrdersByDisplayOrder()
    {
        var first = new EventCategoryView(Guid.NewGuid(), "First", "first", false, 1, true);
        var second = new EventCategoryView(Guid.NewGuid(), "Second", "second", false, 2, true);
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns([second, first]);

        var result = (await BuildController().GetCategories()).Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeAssignableTo<IEnumerable<GuideCategoryApiDto>>().Subject
            .Select(c => c.Id).Should().Equal(first.Id, second.Id);
    }

    [HumansTheory]
    [InlineData(MemberAction.GetPreferences, AccountScenario.Merged)]
    [InlineData(MemberAction.GetPreferences, AccountScenario.Missing)]
    [InlineData(MemberAction.GetPreferences, AccountScenario.Cancelled)]
    [InlineData(MemberAction.UpdatePreferences, AccountScenario.Merged)]
    [InlineData(MemberAction.UpdatePreferences, AccountScenario.Missing)]
    [InlineData(MemberAction.UpdatePreferences, AccountScenario.Cancelled)]
    [InlineData(MemberAction.GetFavourites, AccountScenario.Merged)]
    [InlineData(MemberAction.GetFavourites, AccountScenario.Missing)]
    [InlineData(MemberAction.GetFavourites, AccountScenario.Cancelled)]
    [InlineData(MemberAction.AddFavourite, AccountScenario.Merged)]
    [InlineData(MemberAction.AddFavourite, AccountScenario.Missing)]
    [InlineData(MemberAction.AddFavourite, AccountScenario.Cancelled)]
    [InlineData(MemberAction.RemoveFavourite, AccountScenario.Merged)]
    [InlineData(MemberAction.RemoveFavourite, AccountScenario.Missing)]
    [InlineData(MemberAction.RemoveFavourite, AccountScenario.Cancelled)]
    [InlineData(MemberAction.GetEvents, AccountScenario.Merged)]
    [InlineData(MemberAction.GetEvents, AccountScenario.Missing)]
    [InlineData(MemberAction.GetEvents, AccountScenario.Cancelled)]
    [InlineData(MemberAction.DownloadCsv, AccountScenario.Merged)]
    [InlineData(MemberAction.DownloadCsv, AccountScenario.Missing)]
    [InlineData(MemberAction.DownloadCsv, AccountScenario.Cancelled)]
    public async Task Member_boundaries_resolve_surviving_identity(MemberAction action, AccountScenario account)
    {
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        var controller = BuildController();
        controller.HttpContext.RequestAborted = request.Token;
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, cookieId.ToString())], "test"));
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == AccountScenario.Missing ? null :
                UserInfo.Create(new User { Id = survivorId }, [], [], null));
        });
        _guide.GetExcludedCategorySlugsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns([]);
        _guide.GetFavouritesWithEventsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _guide.GetApprovedEventsAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        _guide.AddFavouriteAsync(Arg.Any<Guid>(), eventId, null, Arg.Any<CancellationToken>()).Returns(AddFavouriteOutcome.Added);
        _guide.RemoveFavouriteAsync(Arg.Any<Guid>(), eventId, null, Arg.Any<CancellationToken>()).Returns(true);
        _guide.GetApprovedEventsForExportAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        var export = new EventsExportController(_guide, _camps, _users)
        {
            ControllerContext = controller.ControllerContext,
        };
        if (account == AccountScenario.Cancelled)
            await request.CancelAsync();
        Func<Task<IActionResult>> invoke = action switch
        {
            MemberAction.GetPreferences => controller.GetPreferences,
            MemberAction.UpdatePreferences => () => controller.UpdatePreferences(new EventsApiController.UpdatePreferencesRequest()),
            MemberAction.GetFavourites => controller.GetFavourites,
            MemberAction.AddFavourite => () => controller.AddFavourite(eventId, null),
            MemberAction.RemoveFavourite => () => controller.RemoveFavourite(eventId, null),
            MemberAction.GetEvents => () => controller.GetEvents(null, null, null, null),
            _ => () => export.DownloadCsv(request.Token),
        };
        var method = action switch
        {
            MemberAction.GetPreferences or MemberAction.GetEvents => nameof(IEventManagement.GetExcludedCategorySlugsAsync),
            MemberAction.UpdatePreferences => nameof(IEventManagement.SavePreferenceAsync),
            MemberAction.GetFavourites => nameof(IEventManagement.GetFavouritesWithEventsAsync),
            MemberAction.AddFavourite => nameof(IEventManagement.AddFavouriteAsync),
            MemberAction.RemoveFavourite => nameof(IEventManagement.RemoveFavouriteAsync),
            _ => nameof(IEventManagement.GetApprovedEventsForExportAsync),
        };

        if (account == AccountScenario.Cancelled)
            await invoke.Should().ThrowAsync<OperationCanceledException>();
        else if (account == AccountScenario.Missing)
            (await invoke()).Should().BeOfType(action == MemberAction.GetEvents ? typeof(OkObjectResult) : typeof(UnauthorizedResult));
        else
        {
            await invoke();
            var call = _guide.ReceivedCalls().Where(call => string.Equals(call.GetMethodInfo().Name, method, StringComparison.Ordinal))
                .Should().ContainSingle().Subject;
            call.GetArguments()[0].Should().Be(survivorId);
            if (action is MemberAction.UpdatePreferences or MemberAction.AddFavourite or MemberAction.RemoveFavourite)
                call.GetArguments()[^1].Should().Be(CancellationToken.None);
            return;
        }

        _guide.ReceivedCalls().Should().NotContain(call => string.Equals(call.GetMethodInfo().Name, method, StringComparison.Ordinal));
    }

    private EventsApiController BuildController()
    {
        var controller = new EventsApiController(_guide, _camps, _users, NullLogger<EventsApiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    // Anonymous PWA caller — no per-user category exclusions.
                    User = new ClaimsPrincipal(new ClaimsIdentity()),
                },
            },
        };
        return controller;
    }

    private static ApprovedEventView MakeEvent(Guid? campId, Guid submitterId, string? host) => new(
        Id: Guid.NewGuid(),
        CampId: campId,
        GuideSharedVenueId: campId == null ? Guid.NewGuid() : null,
        SubmitterUserId: submitterId,
        CategoryId: Guid.NewGuid(),
        CategorySlug: "music",
        CategoryName: "Music",
        CategoryIsSensitive: false,
        VenueName: null,
        Title: "Test Event",
        Description: "Description",
        LocationNote: null,
        Host: host,
        StartAt: Instant.FromUtc(2026, 8, 1, 18, 0),
        DurationMinutes: 60,
        IsRecurring: false,
        RecurrenceDays: null,
        PriorityRank: 0,
        SubmittedAt: Instant.FromUtc(2026, 7, 1, 0, 0),
        LastUpdatedAt: Instant.FromUtc(2026, 7, 1, 0, 0));

    private static UserInfo MakeUserInfo(Guid userId, string burnerName)
    {
        var profile = UserFixtures.Profile(
            burnerName: burnerName,
            firstName: "Test",
            lastName: "Submitter");
        var user = new User
        {
            Id = userId,
            DisplayName = "Test Submitter",
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            BurnerName = burnerName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        return UserInfo.Create(
            user: user,
            userEmails: [],
            externalLogins: [],
            profile: profile);
    }
}
