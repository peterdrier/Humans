using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders the communication-preferences matrix for a given user.
/// Two modes:
///   - <c>readonly: false</c> (self-service, default): toggle checkboxes that POST to
///     <c>/Profile/Me/CommunicationPreferences/Update</c>.
///   - <c>readonly: true</c> (admin view on <c>/Profile/{id}/Admin</c>): values only,
///     plus per-category <c>UpdateSource</c> and <c>UpdatedAt</c> for attribution.
///     No POST forms, no anti-forgery, no submit buttons.
/// </summary>
public sealed class CommunicationPreferencesPanelViewComponent(
    ICommunicationPreferenceService commPrefService,
    ITicketServiceRead ticketQueryService,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool readOnly = false)
    {
        var prefs = await commPrefService.GetPreferencesReadOnlyAsync(userId);
        var prefsByCategory = prefs.ToDictionary(p => p.Category);

        var hasTicketOrder = (await ticketQueryService.GetUserTicketHoldingsAsync(userId))
            .HasTicketAttendeeMatch;

        var categories = new List<CategoryPreferenceItem>();
        foreach (var category in MessageCategoryExtensions.ActiveCategories)
        {
            var pref = prefsByCategory.GetValueOrDefault(category);
            var isAlwaysOn = category.IsAlwaysOn();
            var isTicketingLocked = category == MessageCategory.Ticketing && hasTicketOrder;

            categories.Add(new CategoryPreferenceItem
            {
                Category = category,
                DisplayName = category == MessageCategory.Ticketing
                    ? $"Ticketing — {clock.GetCurrentInstant().InUtc().Year}"
                    : category.ToDisplayName(),
                Description = category.ToDescription(),
                // No row → fall back to the category's domain default (Marketing is
                // opt-out-by-default, so a missing row renders unchecked, matching what
                // the send path treats null as). Other categories default on.
                EmailEnabled = pref is null ? !category.DefaultOptedOut() : !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = !isAlwaysOn && !isTicketingLocked,
                AlertEditable = !isAlwaysOn && !isTicketingLocked,
                Note = isTicketingLocked ? "Locked — you have a ticket for this year" : null,
                UpdateSource = pref?.UpdateSource,
                UpdatedAt = pref?.UpdatedAt,
            });
        }

        return View(new CommunicationPreferencesViewModel
        {
            Categories = categories,
            ReadOnly = readOnly,
        });
    }
}
