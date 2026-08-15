using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The standing proof for the five shared partials G5 lane 4b-i moved out of
/// <c>Humans.UI</c> into their owning sections (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// <para>
/// One class rather than five section files on purpose: each integration test class
/// composes its own host, and the whole lane's exposure is one page plus one lookup.
/// </para>
/// <para>
/// Two failure modes, two assertions. <b>Resolution</b> — a partial is found by name
/// across application parts, not by project reference, so a file that lands in the wrong
/// folder (or in a project Shell does not reference) is invisible to the view engine.
/// That failure throws at render time rather than at build time, so the build proves
/// nothing about it. <b>Localization</b> — every one of these partials read
/// <c>Humans.UI</c>'s <c>SharedResource</c> through <c>Humans.UI/Views/_ViewImports</c>;
/// after the move each one picks up its new section's <c>_ViewImports</c>, where
/// <c>Localizer</c> is the section's own resource set. A call site left on
/// <c>Localizer</c> renders the raw key, in all six languages, on a green 200.
/// </para>
/// <para>
/// <c>/WidgetGallery</c> is the probe page because it renders four of the five against
/// real sample data on one request — the same reason Calendar, Tickets and AuditLog use
/// it. <c>_FavouriteButton</c> is not on it (it needs a published guide event), so the
/// view-engine lookup is what covers that one.
/// </para>
/// </remarks>
public class MovedSharedPartialsRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>Partial name → the section assembly that owns it after lane 4b-i.</summary>
    private static readonly (string Name, string Owner)[] MovedPartials =
    [
        ("_VolunteerProfileBadges", "Humans.Shifts"),
        ("_ShiftsSummaryCard", "Humans.Teams"),
        ("_RoleBadge", "Humans.Teams"),
        ("_FavouriteButton", "Humans.Events"),
        ("_HumanSearchResults", "Humans.Users"),
    ];

    [HumansFact(Timeout = 120000)]
    public void Every_partial_this_lane_moved_out_of_base_still_resolves_by_name()
    {
        using var scope = Factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICompositeViewEngine>();

        // No route values: FindView falls through to the /Views/Shared/{name}.cshtml sweep
        // across every application part, which is exactly the lookup the render sites use.
        var context = new ActionContext(
            new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            new RouteData(),
            new ActionDescriptor());

        var missing = MovedPartials
            .Where(p => !engine.FindView(context, p.Name, isMainPage: false).Success)
            .Select(p => $"{p.Name} (expected in {p.Owner})")
            .ToList();

        missing.Should().BeEmpty(
            because: "a partial the view engine cannot find throws at render time, not at build time");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_widget_gallery_renders_four_of_them_against_real_data()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        await SeedShiftProfileAsync(userId);

        var response = await Client.GetAsync("/WidgetGallery", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Content markers, not tag names: each string is produced only by the partial's own
        // markup against the gallery's sample data, so an empty-state page cannot pass.
        html.Should().Contain("Fire Spinning",
            because: "_VolunteerProfileBadges (Humans.Shifts) renders the seeded skill badge");
        html.Should().Contain("aria-valuenow=\"70\"",
            because: "_ShiftsSummaryCard (Humans.Teams) renders 17 of 24 slots as a progress bar");
        html.Should().Contain("<span class=\"badge bg-warning text-dark\">Coordinator</span>",
            because: "_RoleBadge (Humans.Teams) renders the Coordinator variant");
        html.Should().Contain("love fire dancing and welding",
            because: "_HumanSearchResults (Humans.Users) renders the sample match snippet");

        // The rebind from Localizer to SharedLocalizer: a missed call site renders the key.
        foreach (var key in new[]
                 {
                     "ShiftsSummary_", "Medical_Badge", "Profile_Coordinator", "Teams_Member",
                     "Search_MatchedIn", "Search_NoResults", "MyTeams_View",
                 })
        {
            html.Should().NotContain(key, $"/WidgetGallery rendered the raw key {key}");
        }
    }

    /// <summary>
    /// The gallery renders <c>_VolunteerProfileBadges</c> only when the signed-in human has a
    /// volunteer event profile; without one the card falls back to a "no profile" sentence and
    /// the assertion above would pass on markup the partial never produced. Seeded through the
    /// section's own service rather than <c>ShiftsDbContext</c> for the reason
    /// <c>ShiftsPageRenderTests</c> gives: the cached read path is warmed before the test body.
    /// </summary>
    private async Task SeedShiftProfileAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var shifts = scope.ServiceProvider.GetRequiredService<IShiftManagementService>();

        var profile = await shifts.GetOrCreateShiftProfileAsync(userId);
        profile.Skills = ["Fire Spinning"];
        await shifts.UpdateShiftProfileAsync(profile);
    }
}
