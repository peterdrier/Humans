using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Base.ViewComponents;

/// <summary>
/// Renders one named user-part slot (<see cref="UserPartSlots"/>): every active section's
/// <see cref="IUserPart"/> descriptors for that slot, ordered by weight, each invoked as its
/// declared component <see cref="Type"/> with a <see cref="UserPartArgs"/>. The host page names
/// the slot, the target user and the audience; it holds no per-section state and names no
/// contributor. A contributor that throws is logged and skipped so one section cannot break
/// the page.
/// </summary>
public sealed class UserPartsViewComponent(
    IEnumerable<IUserPart> contributors,
    ILogger<UserPartsViewComponent> logger) : ViewComponent
{
    public IViewComponentResult Invoke(string slot, Guid userId, ProfileCardViewMode viewMode)
    {
        var parts = new List<UserPart>();
        foreach (var contributor in contributors)
        {
            try
            {
                parts.AddRange(contributor.Parts()
                    .Where(p => string.Equals(p.Slot, slot, StringComparison.Ordinal)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load user parts from {Contributor}", contributor.GetType().Name);
            }
        }

        if (parts.Count == 0) return Content(string.Empty);

        return View("Default", new UserPartsViewModel(
            new UserPartArgs(userId, viewMode),
            [.. parts.OrderBy(p => p.Weight).ThenBy(p => p.Component.FullName, StringComparer.Ordinal)]));
    }
}
