using Humans.Base.Interfaces;
using Humans.Cantina.Services.Dtos;

namespace Humans.Cantina.Services;

/// <summary>
/// The medical boundary is an exclusion, not a narrower read: the cached profile carries <c>MedicalConditions</c>, and
/// this service never reads it. The guarantee lives here and in the output records, not in the read contract.
/// </summary>
internal interface ICantinaRosterService : IApplicationService
{
    /// <summary>
    /// A null offset means the week containing today in the event's timezone (zero without an active event). With no
    /// event or nobody on site the DTO is still fully populated, with zero counts and empty lists.
    /// </summary>
    // CSV callers supply their actor so the service records the personal-data disclosure.
    Task<WeeklyRosterDto> GetWeeklyRosterAsync(int? weekStartOffset = null, Guid? exportActorUserId = null, CancellationToken ct = default);

    /// <summary>
    /// A null offset means today in the event's timezone (zero without an active event). People are unsorted.
    /// </summary>
    Task<DailyMatrixDto> GetDailyRosterAsync(int? dayOffset = null, Guid? exportActorUserId = null, CancellationToken ct = default);
}
