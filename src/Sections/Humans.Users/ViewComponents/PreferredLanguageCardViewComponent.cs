using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The "Preferred language" card on the admin dashboard — language distribution across
/// Active ∪ MissingConsents users. Contributed into the admin dashboard's
/// <c>admin-dashboard</c> chrome slot (<see cref="Humans.Users.SectionChrome"/>) since
/// nobodies-collective/Humans#1091.
/// </summary>
/// <remarks>
/// Internal — invoked by name through <c>ChromeSlotViewComponent</c>'s
/// <c>Component.InvokeAsync</c>, not a <c>&lt;vc:&gt;</c> tag helper.
/// </remarks>
internal sealed class PreferredLanguageCardViewComponent(
    IUserServiceRead userService,
    IMembershipCalculatorRead membershipCalculator) : ViewComponent
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
