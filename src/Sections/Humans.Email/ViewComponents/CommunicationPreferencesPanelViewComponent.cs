using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Email.Services;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Email.ViewComponents;

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
            var isAlwaysOn = category.IsAlwaysOn();
            var isTicketingLocked = CommunicationPreferenceService.IsTicketingLocked(category, hasTicketOrder);

            categories.Add(new CategoryPreferenceItem
            {
                Category = category,
                // No row: the category's default, the same rule the send path applies.
                EmailEnabled = pref is null ? !category.DefaultOptedOut() : !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = !isAlwaysOn && !isTicketingLocked,
                AlertEditable = !isAlwaysOn && !isTicketingLocked,
                IsTicketingLocked = isTicketingLocked,
                UpdateSource = pref?.UpdateSource,
                UpdatedAt = pref?.UpdatedAt,
            });
        }

        return View(new CommunicationPreferencesViewModel
        {
            Categories = categories,
            ReadOnly = readOnly,
            TicketingYear = clock.GetCurrentInstant().InUtc().Year,
        });
    }
}
