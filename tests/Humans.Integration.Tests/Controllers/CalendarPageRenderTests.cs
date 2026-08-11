using System.Net;
using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Calendar page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Calendar</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>, so
/// "the page loads" is not the assertion:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build. Calendar's
/// audit panel goes through <c>Component.InvokeAsync("AuditLog", …)</c>, which resolves by name
/// across application parts and throws rather than degrading, so the <c>&lt;vc:&gt;</c> check
/// here guards against a future tag being added rather than anything shipped today.
/// </description></item>
/// <item><description>
/// The resx carve moved all 58 <c>Calendar_*</c> keys out of <c>SharedResource</c>. A key the
/// carve missed, or a call site bound to the wrong set, renders the raw key — in every
/// language, with no error. <c>Common_*</c> and <c>Profile_Location</c> deliberately stayed
/// shared and are read through <c>SharedLocalizer</c>; a mis-bind there is what the
/// "Cancel"/"Location" assertions catch.
/// </description></item>
/// <item><description>
/// The Spanish request is the only thing that proves the section RCL's satellite assemblies
/// reach the host's probing path at all — an English-only check passes either way, because the
/// neutral set is embedded in the main assembly and the fallback is silent.
/// </description></item>
/// </list>
/// <para>
/// Calendar ships no <c>wwwroot/</c>, so there is no static-asset half here.
/// </para>
/// </remarks>
public class CalendarPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly Instant SeriesStart = Instant.FromUtc(2026, 5, 4, 17, 0);
    private static readonly Instant SeriesEnd = Instant.FromUtc(2026, 5, 4, 18, 0);

    /// <summary>
    /// Creates one weekly series through <see cref="ICalendarService"/> rather than straight
    /// through <c>CalendarDbContext</c>: the section registers a Singleton caching decorator
    /// that warms at startup, so a row written behind it is invisible to the window scans the
    /// month/list/agenda pages run, and those are exactly the pages whose copy this test reads.
    /// Each integration test class owns its database (nobodies-collective/Humans#983).
    /// </summary>
    private async Task<(Guid EventId, Guid TeamId)> SeedRecurringEventAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var teams = scope.ServiceProvider.GetRequiredService<ITeamServiceRead>();
        var calendar = scope.ServiceProvider.GetRequiredService<ICalendarService>();

        // Dev login seeds the persona's team membership through the team service, so the
        // cached read already sees at least one active team by the time this runs.
        var team = (await teams.GetTeamsAsync(ct)).Values
            .First(t => t is { IsActive: true, IsHidden: false });

        var created = await calendar.CreateEventAsync(
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

        return (created.Id, team.Id);
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

            // A view component element that the section's _ViewImports failed to bind renders
            // as literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // The fallback for a key the carve missed is the key itself.
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
        // Common_Edit / Common_Delete / Profile_Location are genuinely shared copy and stayed
        // behind; the section's views read them through SharedLocalizer. A mis-bind renders the
        // raw key with a green build, and neither the carve grep nor the build sees it.
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
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var (eventId, _) = await SeedRecurringEventAsync(userId, ct);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // every Calendar page is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync($"/Calendar/Event/{eventId}", ct))
            .Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
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
