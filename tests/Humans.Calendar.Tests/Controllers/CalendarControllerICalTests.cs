using System.Security.Claims;
using AwesomeAssertions;
using Humans.Calendar.Controllers;
using Humans.Calendar.Models;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Calendar.Tests.Controllers;

/// <summary>
/// The personal iCal feed card lives below the month grid on <c>/Calendar</c> (it used to be
/// on <c>/Shifts/Mine</c>). Covers what the controller decides: minting the token on first
/// view, rotating it, and that both act on the viewer's own id and nobody else's.
/// </summary>
public class CalendarControllerICalTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ICalendarServiceRead _calendarRead = Substitute.For<ICalendarServiceRead>();
    private readonly ICalendarService _calendar = Substitute.For<ICalendarService>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();

    private readonly Guid _viewer = Guid.NewGuid();

    public CalendarControllerICalTests()
    {
        _calendarRead
            .GetOccurrencesInWindowAsync(
                Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CalendarOccurrence>());
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
    }

    [HumansFact]
    public async Task Index_mints_a_feed_token_for_a_viewer_who_has_none()
    {
        StubViewer(iCalToken: null);

        var model = await IndexModelAsync();

        await _users.Received(1).SetICalTokenAsync(_viewer, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        model.ICalUrl.Should().Contain($"/api/ical/{_viewer}/");
    }

    [HumansFact]
    public async Task Index_reuses_the_stored_token_and_mints_nothing()
    {
        var token = Guid.NewGuid();
        StubViewer(token);

        var model = await IndexModelAsync();

        await _users.DidNotReceive().SetICalTokenAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        model.ICalUrl.Should().EndWith($"/api/ical/{_viewer}/{token}.ics");
    }

    [HumansFact]
    public async Task Index_renders_without_a_feed_card_when_the_viewer_has_no_user_row()
    {
        _users.GetUserInfoAsync(_viewer, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        var model = await IndexModelAsync();

        model.ICalUrl.Should().BeNull();
        await _users.DidNotReceive().SetICalTokenAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RegenerateIcal_replaces_the_viewers_own_token_and_returns_to_the_grid()
    {
        var oldToken = Guid.NewGuid();
        StubViewer(oldToken);

        var result = await CreateController().RegenerateIcal(Xunit.TestContext.Current.CancellationToken);

        await _users.Received(1).SetICalTokenAsync(
            _viewer,
            Arg.Is<Guid>(t => t != oldToken && t != Guid.Empty),
            Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(CalendarController.Index));
    }

    [HumansFact]
    public async Task RegenerateIcal_challenges_a_viewer_with_no_user_row_and_rotates_nothing()
    {
        _users.GetUserInfoAsync(_viewer, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        var result = await CreateController().RegenerateIcal(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ChallengeResult>();
        await _users.DidNotReceive().SetICalTokenAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private void StubViewer(Guid? iCalToken) =>
        _users.GetUserInfoAsync(_viewer, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                new User { Id = _viewer, State = UserState.Active, PreferredLanguage = "en", ICalToken = iCalToken },
                [], [], [], profile: null, [])));

    private async Task<CalendarMonthViewModel> IndexModelAsync()
    {
        var result = await CreateController()
            .Index(null, null, null, Xunit.TestContext.Current.CancellationToken);
        return result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<CalendarMonthViewModel>().Subject;
    }

    private CalendarController CreateController()
    {
        var localizer = Substitute.For<IStringLocalizer<CalendarResource>>();
        localizer[Arg.Any<string>()].Returns(c => new LocalizedString((string)c[0], (string)c[0]));

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _viewer.ToString())], "test")),
        };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("humans.test");

        return new CalendarController(
            _users,
            _calendarRead,
            _calendar,
            _teams,
            new FakeClock(Instant.FromUtc(2026, 6, 1, 12, 0)),
            localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }
}
