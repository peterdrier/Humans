using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The standing proof for what G5 moved out of <c>Humans.UI</c>: lane 4b-i's shared
/// partials, which went up into their owning sections, and lane 4b-iii B's tag helpers and
/// view components, which went down into <c>Humans.Interfaces</c>
/// (nobodies-collective/Humans#866).
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
/// <c>/WidgetGallery</c> is the probe page because it renders three of them, plus the
/// <c>&lt;vc:user-search-result&gt;</c> that replaced <c>_HumanSearchResults</c>
/// (nobodies-collective/Humans#1062), against real sample data on one request — the same
/// reason Calendar, Tickets and AuditLog use it. <c>_FavouriteButton</c> is not on it (it
/// needs a published guide event), so the view-engine lookup is what covers that one.
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
        // _HumanSearchResults was the fifth until nobodies-collective/Humans#1062 retired it
        // for Users' own <vc:user-search-result>; the gallery assertion below still covers it.
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
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/WidgetGallery", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Content markers, not tag names: each string is produced only by the partial's own
        // markup against the gallery's sample data, so an empty-state page cannot pass.
        html.Should().Contain("Welding",
            because: "_VolunteerProfileBadges (Humans.Shifts) renders WidgetGalleryController's hard-coded sample skill");
        html.Should().Contain("aria-valuenow=\"70\"",
            because: "_ShiftsSummaryCard (Humans.Teams) renders 17 of 24 slots as a progress bar");
        html.Should().Contain("<span class=\"badge bg-warning text-dark\">Coordinator</span>",
            because: "_RoleBadge (Humans.Teams) renders the Coordinator variant");
        // The snippet alone would pass vacuously: unbound, the <vc:> element ships as literal
        // markup and its match-snippet="…" attribute still carries the string. Assert the
        // wrapper the component's own Default.cshtml writes around it instead.
        html.Should().Contain(
            "<span class=\"text-muted ms-1\">...love fire dancing and welding...</span>",
            because: "<vc:user-search-result> (Humans.Users) renders the gallery's sample match "
                   + "snippet — the element only binds through @addTagHelper *, Humans.Users");

        // The rebind from Localizer to SharedLocalizer: a missed call site renders the key.
        foreach (var key in new[]
                 {
                     "ShiftsSummary_", "Medical_Badge", "Profile_Coordinator", "Teams_Member",
                     "Search_MatchedIn",
                 })
        {
            html.Should().NotContain(key, $"/WidgetGallery rendered the raw key {key}");
        }
    }

    /// <summary>
    /// The four Base widgets the gallery renders against real data still bind after lane
    /// 4b-iii B changed the assembly that owns them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tag helpers and view components moved from <c>Humans.UI</c> to
    /// <c>Humans.Interfaces</c> with their namespaces untouched, so every <c>@using</c> and every
    /// C# reference in the repo is unchanged and the compiler has nothing to say. Only
    /// <c>@addTagHelper *, X</c> names an assembly, and only Razor reads it — miss one of the 48
    /// and the element ships as inert literal markup on a green 200, which the browser then
    /// drops without a trace.
    /// </para>
    /// <para>
    /// So every assertion here is on a marker the widget can only have produced by running:
    /// an attribute pair only <c>Views/Shared/Components/Human/Default.cshtml</c> writes, ids
    /// derived from the gallery's own <c>instance-key</c> and <c>name</c>, and — for the
    /// attribute-targeted <c>authorize-policy</c> helper, which emits no markup of its own — the
    /// suppression it exists to perform. Unbound, the bogus-policy span renders like any other
    /// span carrying an unknown attribute, so its absence is the proof.
    /// </para>
    /// <para>
    /// <c>&lt;vc:access-matrix section="Teams"&gt;</c> is asserted here too: the gallery used
    /// to pass <c>section="teams"</c> while <c>AccessMatrixDefinitions.Sections</c> is keyed
    /// <c>"Teams"</c> under <c>StringComparer.Ordinal</c>, so that card rendered empty. Fixed
    /// by correcting the gallery's literal to match the registry's own casing, same as every
    /// other call site.
    /// </para>
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task The_base_tag_helpers_and_view_components_bind_and_render_on_the_gallery()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/WidgetGallery", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain($"data-human-popover=\"true\" data-user-id=\"{userId}\"",
            because: "<vc:human> resolved the signed-in human and its Default.cshtml wrote the "
                   + "popover attributes — nothing else in the tree emits that pair");
        html.Should().Contain("human-search-dropdown-widget-gallery-demo",
            because: "<vc:human-search> derived its dropdown id from the gallery's instance-key");
        html.Should().Contain("widget-gallery-markdown-mde-",
            because: "<markdown-editor> rewrote the element into a textarea with a per-request "
                   + "unique id; the raw tag has no id at all");
        html.Should().Contain("AdminOnly element rendered.",
            because: "the authorize-policy tag helper let the AdminOnly span through for an admin");
        html.Should().NotContain("Bogus policy",
            because: "an unknown policy must fail closed — unbound, the helper suppresses nothing "
                   + "and the span renders like any element with an unrecognised attribute");
        html.Should().Contain("Google resource sync",
            because: "<vc:access-matrix section=\"Teams\"> resolved against "
                   + "AccessMatrixDefinitions.Sections and rendered the Teams-only feature row");

        foreach (var literal in new[]
                 {
                     "<vc:human", "<vc:temp-data-alerts", "<markdown-editor",
                     "<vc:user-search-result",
                 })
        {
            html.Should().NotContain(literal,
                $"GET /WidgetGallery shipped {literal} as literal markup instead of binding it");
        }
    }
}
