using Humans.Base.Interfaces;
using Humans.Users.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The section-contributed parts of a profile page: every active section's
/// <see cref="IUserPart"/> descriptors for the target user, ordered by weight, each rendered
/// as the named view component with that <c>userId</c>. Users holds no per-section state and
/// names no contributor; a contributor that throws is logged and skipped so one section
/// cannot break the page.
/// </summary>
public sealed class UserPartsViewComponent(
    IEnumerable<IUserPart> contributors,
    IServiceProvider services,
    ILogger<UserPartsViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var parts = new List<UserPart>();
        foreach (var contributor in contributors)
        {
            try
            {
                parts.AddRange(await contributor.PartsAsync(services, UserClaimsPrincipal, userId));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load profile parts from {Contributor}", contributor.GetType().Name);
            }
        }

        if (parts.Count == 0) return Content(string.Empty);

        return View("Default", new UserPartsViewModel(
            userId,
            [.. parts.OrderBy(p => p.Weight).ThenBy(p => p.ComponentName, StringComparer.Ordinal)]));
    }
}
