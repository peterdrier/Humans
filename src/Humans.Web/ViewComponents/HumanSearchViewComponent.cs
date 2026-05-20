using Humans.Application.Interfaces.Users;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Inline person picker — visible search box + hidden value input + type-ahead
/// dropdown backed by <c>/api/profiles/search</c>. The canonical inline-picker
/// pattern (see <c>memory/architecture/person-search.md</c>); the typed
/// replacement for the old human-search partial.
/// </summary>
public sealed class HumanSearchViewComponent(IUserService userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string fieldName = "userId",
        string? instanceKey = null,
        string? placeholder = null,
        string? scope = null,
        IEnumerable<Guid>? excludeUserIds = null,
        Guid? selectedUserId = null)
    {
        // Resolve the optional prefill. Render empty if the user doesn't resolve
        // (deleted/rejected) — same "has profile, not rejected" gate the
        // /api/profiles/search by-userid endpoint enforces. The visible value is
        // BurnerName, matching what the search dropdown displays.
        string? selectedDisplayName = null;
        if (selectedUserId is { } id)
        {
            var info = await userService.GetUserInfoAsync(id);
            if (info?.IsActive == true)
            {
                selectedDisplayName = info.BurnerName;
            }
            else
            {
                selectedUserId = null;
            }
        }

        var model = new HumanSearchPickerViewModel
        {
            FieldName = fieldName,
            InstanceKey = instanceKey,
            Placeholder = placeholder,
            Scope = scope,
            ExcludeUserIds = excludeUserIds,
            SelectedUserId = selectedUserId,
            SelectedDisplayName = selectedDisplayName,
        };

        return View("Default", model);
    }
}
