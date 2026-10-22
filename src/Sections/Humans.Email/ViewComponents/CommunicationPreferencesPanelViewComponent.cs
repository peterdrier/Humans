using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Email.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Email.ViewComponents;

public sealed class CommunicationPreferencesPanelViewComponent(
    ICommunicationPreferenceService commPrefService,
    IEnumerable<ICommunicationPreferenceLock> locks,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool readOnly = false)
    {
        var ct = HttpContext.RequestAborted;
        var prefs = await commPrefService.GetPreferencesReadOnlyAsync(userId, ct);
        var locked = await CommunicationPreferenceService.GetLockedCategoriesAsync(locks, userId, ct);

        return View(new CommunicationPreferencesViewModel
        {
            Categories = CategoryPreferenceItem.ForActiveCategories(prefs, locked),
            ReadOnly = readOnly,
            TicketingYear = clock.GetCurrentInstant().InUtc().Year,
        });
    }
}
