using System.Security.Claims;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Cantina.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Humans.Cantina;

/// <summary>Shown while the member has neither a preference nor chose "I don't want to answer".</summary>
internal sealed class SectionThingsToDo : ISectionThingsToDo
{
    public async ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(IServiceProvider services, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return [];

        var profile = await services.GetRequiredService<ICantinaService>().GetDietaryProfileAsync(userId, ct);
        if (profile is { HasPreference: true } or { DeclinedToAnswer: true })
            return [];

        // A failure here is logged by ThingsToDoViewComponent, which drops this contributor's entries.
        var hasQualifyingSignup = await services.GetRequiredService<IShiftService>()
            .HasQualifyingCantinaSignupAsync(userId, ct);
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
