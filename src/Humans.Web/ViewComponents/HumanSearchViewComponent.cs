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
        HumanSearchScope scope = HumanSearchScope.All,
        IEnumerable<Guid>? excludeUserIds = null,
        Guid? selectedUserId = null)
    {
        // Resolve the optional prefill to the user's BurnerName (the same value the
        // dropdown shows). Render empty if the user doesn't resolve (deleted/rejected)
        // — same "has profile, not rejected" gate the /api/profiles/search by-userid
        // endpoint enforces.
        string? selectedBurnerName = null;
        if (selectedUserId is { } id)
        {
            var info = await userService.GetUserInfoAsync(id);
            if (info?.IsActive == true)
            {
                selectedBurnerName = info.BurnerName;
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
            SelectedBurnerName = selectedBurnerName,
        };

        return View("Default", model);
    }
}
