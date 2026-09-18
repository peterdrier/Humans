using System.Security.Claims;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The /Settings tab strip as rendered: every section's <see cref="ISectionSettings"/>
/// contribution, ordered and policy-filtered. Unlike <see cref="AdminNavComposition"/> there
/// are no groups to merge — one section cannot claim a <see cref="SettingsTab.Key"/> another
/// already holds, so a duplicate is dropped rather than merged.
/// </summary>
public static class SettingsTabComposition
{
    public static async Task<IReadOnlyList<SettingsTab>> ComposeAsync(
        IEnumerable<ISectionSettings> contributors,
        IAuthorizationService authorization,
        ClaimsPrincipal user)
    {
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var tabs = new List<SettingsTab>();

        foreach (var tab in contributors.SelectMany(c => c.Tabs()).OrderBy(t => t.Weight))
        {
            if (!seenKeys.Add(tab.Key))
                continue;

            if (tab.Policy is not null)
            {
                var auth = await authorization.AuthorizeAsync(user, null, tab.Policy);
                if (!auth.Succeeded) continue;
            }

            tabs.Add(tab);
        }

        return tabs;
    }
}
