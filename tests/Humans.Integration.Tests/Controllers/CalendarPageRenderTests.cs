using System.Net;
using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Calendar page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: a mis-bound key renders as its name, and only Spanish proves the
/// satellites ship. <c>Common_*</c> and <c>Profile_Location</c> stay shared and are read through
/// <c>SharedLocalizer</c>, which the "Cancel"/"Location" checks guard. The audit panel is invoked by name and throws
/// rather than degrading.
/// </remarks>
public class CalendarPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly Instant SeriesStart = Instant.FromUtc(2026, 5, 4, 17, 0);
    private static readonly Instant SeriesEnd = Instant.FromUtc(2026, 5, 4, 18, 0);

    /// <summary>
    /// Through <see cref="ICalendarService"/>, not <c>CalendarDbContext</c>: the Singleton cache warmed at startup and
    /// would hide a direct write from the window scans these pages run.
    /// </summary>
    private async Task<(Guid EventId, Guid TeamId)> SeedRecurringEventAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var teams = scope.ServiceProvider.GetRequiredService<ITeamService>();
        var calendar = scope.ServiceProvider.GetRequiredService<ICalendarService>();

        // Dev login adds the persona's team through the team service, so the cached read already sees it.
        var team = (await teams.GetTeamsAsync(ct)).Values
            .First(t => t is { IsActive: true, IsHidden: false });

        var created = await calendar.CreateEventWithResultAsync(
            new CreateCalendarEventDto(
                Title: "Community call",
                Description: "Monthly sync",
                Location: "Zoom",
                LocationUrl: "https://meet.google.com/abc",
                OwningTeamId: team.Id,
                StartUtc: SeriesStart,
                EndUtc: SeriesEnd,
                IsAllDay: false,
                RecurrenceRule: "FREQ=WEEKLY;COUNT=6",
                RecurrenceTimezone: "Europe/Madrid"),
            createdByUserId: userId,
            ct: ct);

        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        return (created.Event!.Id, team.Id);
    }

    private static string[] PagesFor(Guid eventId, Guid teamId) =>
    [
        "/Calendar?year=2026&month=5",
        "/Calendar/List?year=2026&month=5",
        "/Calendar/Agenda?from=2026-05-01&to=2026-07-01",
        $"/Calendar/Team/{teamId}?year=2026&month=5",
        $"/Calendar/Event/{eventId}",
        "/Calendar/Event/Create",
        $"/Calendar/Event/{eventId}/Edit",
        $"/Calendar/Event/{eventId}/Occurrence/{OccurrenceSegment(SeriesStart)}/Edit",
    ];

    private static string OccurrenceSegment(Instant instant) =>
        Uri.EscapeDataString(InstantPattern.ExtendedIso.Format(instant));

    [HumansFact(Timeout = 120000)]
    public async Task Every_calendar_page_renders_without_raw_resource_keys_or_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var (eventId, teamId) = await SeedRecurringEventAsync(userId, ct);

        foreach (var url in PagesFor(eventId, teamId))
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            html.Should().NotContain("Calendar_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_month_view_renders_the_sections_own_copy_and_the_seeded_series()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        await SeedRecurringEventAsync(userId, ct);

        var html = await (await Client.GetAsync("/Calendar?year=2026&month=5", ct))
            .Content.ReadAsStringAsync(ct);

        html.Should().Contain("Today",
            because: "Calendar_Today resolves through IStringLocalizer<CalendarResource>");
        html.Should().Contain("Agenda", because: "Calendar_Agenda is on the view pills");
        html.Should().Contain("Community call",
            because: "the cached window scan must expand the seeded weekly series");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_event_page_renders_the_shared_keys_that_stayed_in_SharedResource()
    {
        // Shared keys the section reads through SharedLocalizer; a mis-bind renders the raw key on a green build.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var (eventId, _) = await SeedRecurringEventAsync(userId, ct);

        var html = await (await Client.GetAsync($"/Calendar/Event/{eventId}", ct))
            .Content.ReadAsStringAsync(ct);

        html.Should().NotContain("Common_");
        html.Should().NotContain("Profile_Location");
        html.Should().Contain("Location", because: "Profile_Location resolves through SharedResource");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Calendar_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var (eventId, _) = await SeedRecurringEventAsync(userId, ct);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync($"/Calendar/Event/{eventId}", ct))
            .Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Calendar/Event/Create", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Nuevo evento");      // Calendar_NewEvent
        html.Should().Contain("Equipo propietario");// Calendar_OwningTeam
        html.Should().NotContain("New event");
        html.Should().NotContain("Calendar_");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Shell_renders_the_sections_user_calendar_component()
    {
        // An @using doesn't import the tag helper: Shell needs its own @addTagHelper *, Humans.Calendar and the
        // component must stay public, or the element ships inert. The card header renders only if the component ran,
        // even for an empty feed.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Debug/WidgetGallery", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Calendar Feed", "the component renders its own card header");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Users_admin_detail_resolves_the_component_view_across_the_rcl_boundary()
    {
        // Invoked by name, but Default.cshtml lives in the Calendar RCL, so the view lookup is the cross-assembly half.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync($"/Users/Admin/{userId}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Calendar Feed", "the component renders its own card header");
        html.Should().Contain("feed not provisioned",
            "the persona has never minted a feed token, and that line comes from the moved view");
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["token"].Value : null;
    }
}
