using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Email.ViewComponents;

/// <summary>
/// Renders the communication-preferences matrix for a given user.
/// Two modes:
///   - <c>readonly: false</c> (self-service, default): toggle checkboxes that POST to
///     <c>/Email/CommunicationPreferences/Update</c>.
///   - <c>readonly: true</c> (admin view on <c>/Users/Admin/{id}</c>): values only,
///     plus per-category <c>UpdateSource</c> and <c>UpdatedAt</c> for attribution.
///     No POST forms, no anti-forgery, no submit buttons.
///
/// Public, not internal: Razor's build-time tag-helper discovery only sees public view
/// components (design; HUM0034 exempts view components), used in-assembly via
/// <c>&lt;vc:communication-preferences-panel&gt;</c>.
/// </summary>
public sealed class CommunicationPreferencesPanelViewComponent(
    ICommunicationPreferenceService commPrefService,
    ITicketService ticketQueryService,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool readOnly = false)
    {
        var ct = HttpContext.RequestAborted;
        var prefs = await commPrefService.GetPreferencesReadOnlyAsync(userId, ct);
        var prefsByCategory = prefs.ToDictionary(p => p.Category);

        var hasTicketOrder = (await ticketQueryService.GetUserTicketHoldingsAsync(userId, ct))
            .HasTicketAttendeeMatch;

        var categories = new List<CategoryPreferenceItem>();
        foreach (var category in MessageCategoryExtensions.ActiveCategories)
        {
            var pref = prefsByCategory.GetValueOrDefault(category);
            var item = CategoryPreferenceItem.FromPreference(category, pref, hasTicketOrder);
            item.UpdateSource = pref?.UpdateSource;
            item.UpdatedAt = pref?.UpdatedAt;
            categories.Add(item);
        }

        return View(new CommunicationPreferencesViewModel
        {
            Categories = categories,
            ReadOnly = readOnly,
            TicketingYear = clock.GetCurrentInstant().InUtc().Year,
        });
    }
}
