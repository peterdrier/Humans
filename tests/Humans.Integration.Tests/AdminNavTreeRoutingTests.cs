using Humans.Base.ViewComponents;
using Humans.Base.Interfaces;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests;

/// <summary>
/// Every composed admin nav entry (<see cref="AdminNavComposition"/>) must name a
/// controller/action pair that actually exists in the running app's routing table.
/// </summary>
/// <remarks>
/// A silent failure with no other guard: when the pair resolves to no endpoint, the anchor tag helper omits the
/// <c>href</c> and the page still returns 200 with a link that goes nowhere. Moving an action between controllers
/// under one <c>[Route]</c> prefix changes no URL and does exactly this.
/// <para>
/// Reads <c>IActionDescriptorCollectionProvider</c> rather than reflecting over controllers, so it sees what MVC
/// really routes, including internal section controllers that reach the table only via
/// <c>SectionControllerFeatureProvider</c>.
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

        // Host startup is the slow part this timeout covers; stop here if it used the budget.
        Xunit.TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

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
