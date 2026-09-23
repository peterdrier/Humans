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
/// on <c>/Shifts/Mine</c>). Covers what the controller decides: asking for the token on first
/// view, rotating it, and that both act on the viewer's own id and nobody else's. The token's
/// own lifecycle is <see cref="CalendarFeedTokenService"/>'s and is tested there.
/// </summary>
public class CalendarControllerICalTests
{
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ICalendarFeedTokenService _feedTokens = Substitute.For<ICalendarFeedTokenService>();
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
    public async Task Index_asks_for_the_viewers_token_and_renders_it_as_the_feed_url()
    {
        StubViewer();
        var token = Guid.NewGuid();
        _feedTokens.EnsureAsync(_viewer, Arg.Any<CancellationToken>()).Returns(token);

        var model = await IndexModelAsync();

        await _feedTokens.Received(1).EnsureAsync(_viewer, Arg.Any<CancellationToken>());
        model.ICalUrl.Should().EndWith($"/api/ical/{_viewer}/{token}.ics");
    }

    [HumansFact]
    public async Task Index_never_rotates_the_token_it_renders()
    {
        StubViewer();
        _feedTokens.EnsureAsync(_viewer, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        await IndexModelAsync();

        await _feedTokens.DidNotReceive().RotateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_renders_without_a_feed_card_when_the_viewer_has_no_user_row()
    {
        _users.GetUserInfoAsync(_viewer, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        var model = await IndexModelAsync();

        model.ICalUrl.Should().BeNull();
        await _feedTokens.DidNotReceive().EnsureAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_does_not_load_teams_for_an_unused_month_model_field()
    {
        StubViewer();
        _feedTokens.EnsureAsync(_viewer, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        await IndexModelAsync();

        await _teams.DidNotReceive().GetTeamsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RegenerateIcal_rotates_the_viewers_own_token_and_returns_to_the_grid()
    {
        StubViewer();

        var result = await CreateController().RegenerateIcal(Xunit.TestContext.Current.CancellationToken);

        await _feedTokens.Received(1).RotateAsync(_viewer, Arg.Any<CancellationToken>());
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
        await _feedTokens.DidNotReceive().RotateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private void StubViewer() =>
        _users.GetUserInfoAsync(_viewer, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                new User { Id = _viewer, State = UserState.Active, PreferredLanguage = "en" },
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
            _feedTokens,
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
