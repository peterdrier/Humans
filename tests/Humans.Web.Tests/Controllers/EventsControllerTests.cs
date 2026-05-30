using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Authorization coverage for the individual-event edit route
/// (<c>GET/POST /Events/Submit/{eventId}/Edit</c>). The "Edit event" link in
/// moderation lifecycle emails points here. It must serve the submitter and
/// Events admins, return 403 (not 404) for any other signed-in user, and 404
/// only when the event genuinely doesn't exist on this route.
/// </summary>
public class EventsControllerTests
{
    private readonly IEventService _guide = Substitute.For<IEventService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly IAuthorizationService _authz = Substitute.For<IAuthorizationService>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IClock _clock = Substitute.For<IClock>();

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

    private Guid StubEvent(Guid submitterId, Guid? campId, EventStatus status)
    {
        var guideEvent = MakeEvent(campId, submitterId, status);
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>())
            .Returns(guideEvent);
        return guideEvent.Id;
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
            .Returns((IReadOnlyList<EventCategoryView>)[]);
        _guide.GetActiveVenuesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<EventVenueView>)[]);
    }

    private EventsController BuildController(Guid currentUserId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, currentUserId.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        _users.GetUserInfoAsync(currentUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(currentUserId, "Current User")));

        return new EventsController(_guide, _users, _camps, _authz, _clock, _email, Substitute.For<IEmailMessageFactory>(), NullLogger<EventsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static BurnSettingsInfo MakeBurnSettings() => new(
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
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = burnerName,
            FirstName = "Test",
            LastName = "User",
            IsApproved = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        var user = new User
        {
            Id = userId,
            DisplayName = "Test User",
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
