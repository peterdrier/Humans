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

/// <summary>Shared partials owned by sections, and Base's tag helpers and view components, still bind.</summary>
/// <remarks>
/// <para>
/// A partial resolves by name across application parts, so one in the wrong folder or project throws at render time,
/// not build time. Each partial now reads its section's <c>_ViewImports</c>, where <c>Localizer</c> is the section's
/// own set, so a call site left on it renders the raw key on a green 200.
/// </para>
/// <para>
/// <c>/Debug/WidgetGallery</c> renders three of them against sample data on one request. <c>_FavouriteButton</c> needs
/// a published guide event, so only the view-engine lookup covers it.
/// </para>
/// </remarks>
public class MovedSharedPartialsRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>Partial name to the section assembly that owns it.</summary>
    private static readonly (string Name, string Owner)[] MovedPartials =
    [
        ("_VolunteerProfileBadges", "Humans.Shifts"),
        ("_ShiftsSummaryCard", "Humans.Teams"),
        ("_RoleBadge", "Humans.Teams"),
        ("_FavouriteButton", "Humans.Events"),
    ];

    [HumansFact(Timeout = 120000)]
    public void Every_partial_this_lane_moved_out_of_base_still_resolves_by_name()
    {
        using var scope = Factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICompositeViewEngine>();

        // No route values: FindView falls through to the /Views/Shared sweep across every part, as the render sites do.
        var context = new ActionContext(
            new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            new RouteData(),
            new ActionDescriptor());

        // Host startup is the slow part; stop here if it used the budget.
        Xunit.TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

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

        var response = await Client.GetAsync("/Debug/WidgetGallery", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Content only each partial's markup produces from the sample data, so an empty-state page can't pass.
        html.Should().Contain("Welding",
            because: "_VolunteerProfileBadges (Humans.Shifts) renders WidgetGalleryController's hard-coded sample skill");
        html.Should().Contain("aria-valuenow=\"70\"",
            because: "_ShiftsSummaryCard (Humans.Teams) renders 17 of 24 slots as a progress bar");
        html.Should().Contain("<span class=\"badge bg-warning text-dark\">Coordinator</span>",
            because: "_RoleBadge (Humans.Teams) renders the Coordinator variant");
        // Unbound, the element's match-snippet attribute still carries the snippet, so assert the wrapper the
        // component's Default.cshtml writes.
        html.Should().Contain(
            "<span class=\"text-muted ms-1\">...love fire dancing and welding...</span>",
            because: "<vc:user-search-result> (Humans.Users) renders the gallery's sample match "
                   + "snippet — the element only binds through @addTagHelper *, Humans.Users");

        // A call site missed in the Localizer to SharedLocalizer rebind renders the key.
        foreach (var key in new[]
                 {
                     "ShiftsSummary_", "Medical_Badge", "Profile_Coordinator", "Teams_Member",
                     "Search_MatchedIn",
                 })
        {
            html.Should().NotContain(key, $"/Debug/WidgetGallery rendered the raw key {key}");
        }
    }

    /// <summary>The four Base widgets the gallery renders still bind.</summary>
    /// <remarks>
    /// <para>
    /// Only <c>@addTagHelper *, X</c> names an assembly, and only Razor reads it; a miss ships inert markup on a green
    /// 200. So each assertion is on a marker the widget produces only by running. The <c>authorize-policy</c> helper
    /// emits nothing, so the suppression of the bogus-policy span is its proof.
    /// </para>
    /// <para>
    /// The access-matrix key is matched ordinally, so <c>section="teams"</c> would render an empty card.
    /// </para>
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task The_base_tag_helpers_and_view_components_bind_and_render_on_the_gallery()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Debug/WidgetGallery", ct);
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
            because: "<vc:access-matrix section=\"Teams\"> resolved against the contributed "
                   + "access matrices and rendered the Teams-only feature row");

        foreach (var literal in new[]
                 {
                     "<vc:human", "<vc:temp-data-alerts", "<markdown-editor",
                     "<vc:user-search-result",
                 })
        {
            html.Should().NotContain(literal,
                $"GET /Debug/WidgetGallery shipped {literal} as literal markup instead of binding it");
        }
    }
}
