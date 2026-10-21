using System.Security.Claims;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Cantina.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Humans.Cantina;

/// <summary>
/// Cantina's entry on the member dashboard's things-to-do list: the dietary nudge, shown while the
/// member has neither a preference on file nor chose "I don't want to answer".
/// Spec: Docs/features/dietary-nudge.md (US-35.1).
/// </summary>
internal sealed class SectionThingsToDo : ISectionThingsToDo
{
    public async ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(IServiceProvider services, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return [];

        var profile = await services.GetRequiredService<ICantinaService>().GetDietaryProfileAsync(userId);
        if (profile is { HasPreference: true } or { DeclinedToAnswer: true })
            return [];

        // Copy varies by whether a qualifying shift already needs feeding; the entry is the same either way.
        // A failure here is logged by ThingsToDoViewComponent, which drops this contributor's entries.
        var hasQualifyingSignup = await services.GetRequiredService<IShiftService>()
            .HasQualifyingCantinaSignupAsync(userId);
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        return
        [
            new ThingsToDoEntry("dietary", localizer["Todo_DietaryMedical_Title"].Value,
                "fa-solid fa-utensils", Controller: "CantinaProfile", Action: "Dietary", Weight: 50)
            {
                Description = localizer[hasQualifyingSignup
                    ? "Todo_DietaryMedical_Pending"
                    : "Todo_DietaryMedical_NoShift_Pending"].Value,
                ActionText = localizer["Todo_DietaryMedical_Action"].Value,
            },
        ];
    }
}
