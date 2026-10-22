using Humans.Email.Contracts;
using Humans.Email.Models;
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
