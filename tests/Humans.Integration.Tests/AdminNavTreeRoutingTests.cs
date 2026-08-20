using Humans.Base.Interfaces;
using Humans.Integration.Tests.Infrastructure;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests;

/// <summary>
/// Every composed admin nav entry (<see cref="AdminNavComposition"/>) must name a
/// controller/action pair that actually exists in the running app's routing table.
/// </summary>
/// <remarks>
/// This is a silent failure with no other guard. <c>AdminSidebar</c>'s view renders each item
/// through an anchor tag helper with <c>asp-controller</c>/<c>asp-action</c>; when the pair
/// resolves to no endpoint, <c>IUrlHelper.Action</c> returns null, the tag helper omits the
/// <c>href</c> entirely, and the page still returns 200 with a link-shaped element that goes
/// nowhere. Build, suite and even the §15 step 12 HTML diff all pass.
/// <para>
/// Written after A2 (peterdrier/Humans#1239) hit exactly that: splitting
/// <c>FinanceController</c> moved its <c>Index</c> action into <c>BudgetAdminController</c> —
/// same <c>[Route("Finance")]</c> prefix, so no URL changed — while this table still said
/// controller "Finance", leaving finance admins with no way into the finance overview. Every
/// G5 section move rehomes controllers, so this is a recurring shape, not a one-off.
/// </para>
/// <para>
/// Reads <c>IActionDescriptorCollectionProvider</c> rather than reflecting over controller
/// types, so it sees what MVC really routes — including section controllers, which are
/// <c>internal</c> and reach the table only via <c>SectionControllerFeatureProvider</c>
/// (memory/architecture/section-controllers-need-feature-provider.md).
/// </para>
/// </remarks>
public class AdminNavTreeRoutingTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60000)]
    public void Every_admin_nav_entry_resolves_to_a_real_action()
    {
        var actions = Factory.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .Select(a =>
            {
                a.RouteValues.TryGetValue("controller", out var controller);
                a.RouteValues.TryGetValue("action", out var action);
                return (Controller: controller, Action: action);
            })
            .Where(x => x.Controller is not null && x.Action is not null)
            .ToHashSet();

        var groups = AdminNavComposition.Compose(
            Factory.Services.GetRequiredService<IEnumerable<ISectionAdminNav>>());

        var unresolvable = groups
            .SelectMany(g => g.Items)
            // RawHref items are absolute or external links and bypass the tag helper entirely.
            .Where(i => i.RawHref is null && !string.IsNullOrEmpty(i.Controller))
            .Where(i => !actions.Contains((i.Controller, i.Action)))
            .Select(i => $"{i.Label}: {i.Controller}/{i.Action}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unresolvable.Count == 0,
            "Admin sidebar entries naming a controller/action that does not exist — each renders "
            + "as a link with no href:\n  " + string.Join("\n  ", unresolvable));
    }
}
