using System.Security.Claims;
using AwesomeAssertions;
using Humans.Calendar.Controllers;
using Humans.Calendar.Models;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Humans.Base;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly InMemorySession _session = new();
    private Instant _now = Instant.FromUtc(2026, 6, 1, 12, 0);

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
    public async Task Index_uses_the_browser_timezone_for_calendar_windows()
    {
        _session.SetString(DateTimeDisplayExtensions.SessionKey, "America/Los_Angeles");

        var model = await IndexModelAsync();

        model.ViewerTimezoneLabel.Should().Be("America/Los_Angeles");
    }

    [HumansFact]
    public async Task Create_defaults_to_the_browser_local_date_and_zone()
    {
        _now = Instant.FromUtc(2026, 6, 1, 23, 0);
        _session.SetString(DateTimeDisplayExtensions.SessionKey, "Asia/Tokyo");
        var team = Guid.NewGuid();
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>
            {
                [team] = new(team, "Team", null, "team", true, false, SystemTeamType.None,
                    false, false, false, false, Instant.MinValue, [])
            });

        var result = await CreateController().Create((Guid?)null, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<CalendarEventFormViewModel>().Subject;
        model.StartLocal.Should().Be(new DateTime(2026, 6, 2, 19, 0, 0));
        model.EndDateLocal.Should().Be(new DateTime(2026, 6, 2));
        model.RecurrenceTimezone.Should().Be("Asia/Tokyo");
    }

    [HumansFact]
    public async Task Edit_renders_a_one_off_event_in_the_viewers_zone()
    {
        _session.SetString(DateTimeDisplayExtensions.SessionKey, "Asia/Tokyo");
        var id = Guid.NewGuid();
        _calendarRead.GetEventByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new CalendarEventDetail(
            id, "Talk", null, null, null, Guid.NewGuid(),
            Instant.FromUtc(2026, 6, 1, 10, 0), Instant.FromUtc(2026, 6, 1, 11, 0),
            IsAllDay: false, RecurrenceRule: null, RecurrenceTimezone: null, _now, _now));

        var result = await CreateController().Edit(id, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<CalendarEventFormViewModel>().Subject;
        model.StartLocal.Should().Be(new DateTime(2026, 6, 1, 19, 0, 0));
        model.RecurrenceTimezone.Should().Be("Asia/Tokyo");
    }

    [HumansFact]
    public async Task EditOccurrence_reads_override_times_in_the_posted_zone()
    {
        var id = Guid.NewGuid();
        var original = Instant.FromUtc(2026, 6, 2, 17, 0);
        _calendarRead.GetEventByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new CalendarEventDetail(
            id, "Weekly", null, null, null, Guid.NewGuid(), original, original.Plus(Duration.FromHours(1)),
            IsAllDay: false, RecurrenceRule: "FREQ=WEEKLY", RecurrenceTimezone: "Europe/Madrid", _now, _now));

        await CreateController().EditOccurrence(id, "2026-06-02T17:00:00Z", new OccurrenceOverrideFormViewModel
        {
            OverrideStartLocal = new DateTime(2026, 6, 3, 19, 0, 0),
            RecurrenceTimezone = "Asia/Tokyo",
        }, Xunit.TestContext.Current.CancellationToken);

        await _calendar.Received(1).OverrideOccurrenceAsync(id, original,
            Arg.Is<OverrideOccurrenceDto>(d => d.OverrideStartUtc == Instant.FromUtc(2026, 6, 3, 10, 0)),
            _viewer, Arg.Any<CancellationToken>(), null);
    }

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    public async Task EditOccurrence_rejects_invalid_text_before_writing_in_every_culture(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var id = Guid.NewGuid();
        var original = Instant.FromUtc(2026, 6, 2, 17, 0);
        _calendarRead.GetEventByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new CalendarEventDetail(
            id, "Weekly", null, null, null, Guid.NewGuid(), original, original.Plus(Duration.FromHours(1)),
            IsAllDay: false, RecurrenceRule: "FREQ=WEEKLY", RecurrenceTimezone: "Europe/Madrid", _now, _now));

        async Task AssertRejected(OccurrenceOverrideFormViewModel form, string field, string key, params object[] arguments)
        {
            var controller = CreateController();
            controller.HttpContext.RequestServices = services;
            controller.Url = Substitute.For<IUrlHelper>();
            validator.Validate(controller.ControllerContext, null, "", form);
            controller.ModelState.ContainsKey(field).Should().BeTrue();
            var expected = localizer[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            controller.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
            var result = await controller.EditOccurrence(id, "2026-06-02T17:00:00Z", form,
                Xunit.TestContext.Current.CancellationToken);
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        }

        await AssertRejected(new() { OverrideTitle = new string('x', 201) }, "OverrideTitle", "Validation_MaxLength", "", 200);
        await AssertRejected(new() { OverrideDescription = new string('x', 4001) }, "OverrideDescription", "Validation_MaxLength", "", 4000);
        await AssertRejected(new() { OverrideLocation = new string('x', 501) }, "OverrideLocation", "Validation_MaxLength", "", 500);
        await AssertRejected(new() { OverrideLocationUrl = "https://example.com/" + new string('x', 2000) }, "OverrideLocationUrl", "Validation_MaxLength", "", 2000);
        await AssertRejected(new() { OverrideLocationUrl = "invalid" }, "OverrideLocationUrl", "Validation_InvalidValue");
        await _calendar.DidNotReceive().OverrideOccurrenceAsync(Arg.Any<Guid>(), Arg.Any<Instant?>(),
            Arg.Any<OverrideOccurrenceDto>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<LocalDate?>());

        foreach (var form in new[]
        {
            new OccurrenceOverrideFormViewModel(),
            new OccurrenceOverrideFormViewModel
            {
                OverrideTitle = new string('x', 200), OverrideDescription = new string('x', 4000),
                OverrideLocation = new string('x', 500), OverrideLocationUrl = "https://example.com/" + new string('x', 2000 - "https://example.com/".Length),
            },
        })
        {
            var controller = CreateController();
            controller.HttpContext.RequestServices = services;
            controller.Url = Substitute.For<IUrlHelper>();
            validator.Validate(controller.ControllerContext, null, "", form);
            controller.ModelState.IsValid.Should().BeTrue();
            (await controller.EditOccurrence(id, "2026-06-02T17:00:00Z", form,
                Xunit.TestContext.Current.CancellationToken)).Should().BeOfType<RedirectToActionResult>();
        }
        await _calendar.Received(2).OverrideOccurrenceAsync(id, original,
            Arg.Any<OverrideOccurrenceDto>(), _viewer, Arg.Any<CancellationToken>(), null);
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
        http.Features.Set<ISessionFeature>(new TestSessionFeature(_session));

        return new CalendarController(
            _users,
            _feedTokens,
            _calendarRead,
            _calendar,
            _teams,
            new FakeClock(_now),
            localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private sealed class InMemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;
        public string Id => "calendar-test";
        public IEnumerable<string> Keys => _values.Keys;
        public void Clear() => _values.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }

    private sealed class TestSessionFeature(ISession session) : ISessionFeature
    {
        public ISession Session { get; set; } = session;
    }
}
