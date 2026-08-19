using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Humans.Governance;

/// <summary>
/// Governance's entry on the member dashboard's things-to-do list: the required consents
/// the membership snapshot says are still outstanding. No required documents, no entry.
/// </summary>
internal sealed class SectionThingsToDo : ISectionThingsToDo
{
    public async ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return [];
        }

        var snapshot = await services.GetRequiredService<IMembershipCalculatorRead>()
            .GetMembershipSnapshotAsync(userId);
        if (snapshot.RequiredConsentCount == 0)
        {
            return [];
        }

        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var complete = snapshot.PendingConsentCount == 0;

        return
        [
            new ThingsToDoEntry("consents", localizer["Todo_Consents_Title"].Value,
                "fa-solid fa-file-signature", Controller: "Consent", Action: "Index", Weight: 20)
            {
                Description = complete
                    ? localizer["Todo_Consents_Done"].Value
                    : string.Format(CultureInfo.CurrentCulture, localizer["Todo_Consents_Pending"].Value,
                        snapshot.PendingConsentCount, snapshot.RequiredConsentCount),
                IsDone = complete,
                ActionText = localizer["Todo_Consents_Action"].Value,
            }
        ];
    }
}
