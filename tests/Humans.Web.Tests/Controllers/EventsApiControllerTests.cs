using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers.Api;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Host-attribution coverage for <see cref="EventsApiController"/> (US-26.3 / US-26.6).
/// The published-guide host falls back to the submitter's burner name for individual
/// events when no explicit host is set; camp events show their own host as-is and never
/// fall back to a submitter.
/// </summary>
public class EventsApiControllerTests
{
    private readonly IEventService _guide = Substitute.For<IEventService>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    public EventsApiControllerTests()
    {
        // No guide settings → no event-settings/timezone/gate-date lookups in the action.
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventGuideSettingsView?)null);
    }

    [HumansFact]
    public async Task GetEvents_IndividualEventWithoutHost_FallsBackToSubmitterBurnerName()
    {
        var submitterId = Guid.NewGuid();
        _users.GetUserInfoAsync(submitterId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(submitterId, "Fire Dancer")));
        StubApprovedEvents(MakeEvent(campId: null, submitterId, host: null));

        var dto = await SingleResultAsync();

        dto.Host.Should().Be("Fire Dancer");
    }

    [HumansFact]
    public async Task GetEvents_IndividualEventWithHost_UsesHost()
    {
        var submitterId = Guid.NewGuid();
        _users.GetUserInfoAsync(submitterId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(submitterId, "Fire Dancer")));
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

    private EventsApiController BuildController()
    {
        var controller = new EventsApiController(_guide, _camps, _users)
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
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = burnerName,
            FirstName = "Test",
            LastName = "Submitter",
            IsApproved = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        var user = new User
        {
            Id = userId,
            DisplayName = "Test Submitter",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        };
        return UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }
}
