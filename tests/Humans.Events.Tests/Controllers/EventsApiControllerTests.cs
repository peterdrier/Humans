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

    [HumansTheory]
    [InlineData("Events", "Guide")]
    [InlineData("Events", "Burn")]
    [InlineData("Events", "Excluded")]
    [InlineData("Events", "Approved")]
    [InlineData("Events", "Camps")]
    [InlineData("Events", "Submitter")]
    [InlineData("Event", "Guide")]
    [InlineData("Event", "Event")]
    [InlineData("Event", "Submitter")]
    [InlineData("Barrios", "Guide")]
    [InlineData("Barrios", "Approved")]
    [InlineData("Barrio", "Guide")]
    [InlineData("Barrio", "Approved")]
    [InlineData("Categories", "Categories")]
    [InlineData("Preferences", "Excluded")]
    [InlineData("Favourites", "Guide")]
    [InlineData("Favourites", "Favourites")]
    [InlineData("Favourites", "Camps")]
    public async Task Read_AbandonedRequest_CancelsAtLookup(string route, string boundary)
    {
        using var request = new CancellationTokenSource();
        async Task<T> ReadAsync<T>(T value, string current, CancellationToken ct)
        {
            if (string.Equals(current, boundary, StringComparison.Ordinal))
                await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return value;
        }
        var controller = BuildController();
        controller.HttpContext.RequestAborted = request.Token;
        var userId = Guid.NewGuid();
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var burn = BurnFixtures.Burn(year: 2026);
        var settings = new EventGuideSettingsView(Guid.NewGuid(), burn.Id, Instant.MinValue,
            Instant.MaxValue, Instant.MaxValue, 100, "Europe/Madrid", Instant.MinValue, Instant.MinValue);
        var approved = MakeEvent(null, Guid.NewGuid(), null);
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventGuideSettingsView?>(settings, "Guide", call.Arg<CancellationToken>()));
        _guide.GetEventSettingsByIdAsync(burn.Id, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventSettingsInfo?>(burn, "Burn", call.Arg<CancellationToken>()));
        _guide.GetExcludedCategorySlugsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<List<string>>([], "Excluded", call.Arg<CancellationToken>()));
        _guide.GetApprovedEventsAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<ApprovedEventView>>([approved], "Approved", call.Arg<CancellationToken>()));
        _guide.GetApprovedEventByIdAsync(approved.Id, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<ApprovedEventView?>(approved, "Event", call.Arg<CancellationToken>()));
        _camps.GetCampsForYearAsync(burn.GateOpeningDate.Year, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<CampInfo>>([], "Camps", call.Arg<CancellationToken>()));
        _users.GetUserInfoAsync(approved.SubmitterUserId, Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<UserInfo?>(ReadAsync<UserInfo?>(MakeUserInfo(approved.SubmitterUserId, "Host"),
                "Submitter", call.Arg<CancellationToken>())));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(ReadAsync<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [approved.SubmitterUserId] = MakeUserInfo(approved.SubmitterUserId, "Host") },
                "Submitter", call.Arg<CancellationToken>())));
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventCategoryView>>([], "Categories", call.Arg<CancellationToken>()));
        _guide.GetFavouritesWithEventsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventFavouriteInfo>>([], "Favourites", call.Arg<CancellationToken>()));
        Func<Task<IActionResult>> act = route switch
        {
            "Events" => () => controller.GetEvents(null, null, null, null),
            "Event" => () => controller.GetEvent(approved.Id),
            "Barrios" => controller.GetBarrios,
            "Barrio" => () => controller.GetBarrio(Guid.NewGuid()),
            "Categories" => controller.GetCategories,
            "Preferences" => controller.GetPreferences,
            "Favourites" => controller.GetFavourites,
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
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

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddFavourite_ReturnsClientErrorForInvalidTargets(bool invalidDay)
    {
        var controller = BuildController();
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        _guide.AddFavouriteAsync(userId, eventId, 999, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(invalidDay
                ? new ArgumentOutOfRangeException(nameof(invalidDay)) : new KeyNotFoundException()));

        var result = await controller.AddFavourite(eventId, 999);

        result.Should().BeOfType(invalidDay ? typeof(BadRequestResult) : typeof(NotFoundResult));
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
            lastName: "Submitter",
            isApproved: true);
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
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            communicationPreferences: []);
    }
}
