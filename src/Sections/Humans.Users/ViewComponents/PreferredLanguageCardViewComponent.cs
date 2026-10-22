using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>The admin dashboard's "Preferred language" card: Active ∪ MissingConsents users.</summary>
/// <remarks>
/// Internal: invoked by Type through <c>ChromeSlotViewComponent</c>, not a <c>&lt;vc:&gt;</c> tag helper.
/// </remarks>
internal sealed class PreferredLanguageCardViewComponent(
    IUserService userService,
    IMembershipCalculator membershipCalculator) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var snapshot = await userService.GetAllUserInfosAsync(ct);
        var allUserIds = snapshot.Select(u => u.Id).ToList();
        var partition = await membershipCalculator.PartitionUsersAsync(allUserIds, ct);

        var languages = PreferredLanguageCalculator.Build(snapshot, partition);
        return languages.Count == 0 ? Content(string.Empty) : View(languages);
    }
}
