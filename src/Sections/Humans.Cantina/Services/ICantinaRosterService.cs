using Humans.AuditLog.Contracts;
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
    // Disclosing callers (CSV download, allergy reveal) say who, so the service records the personal-data disclosure.
    Task<WeeklyRosterDto> GetWeeklyRosterAsync(int? weekStartOffset = null, RosterDisclosure? disclosure = null, CancellationToken ct = default);

    /// <summary>
    /// A null offset means today in the event's timezone (zero without an active event). People are unsorted.
    /// </summary>
    Task<DailyMatrixDto> GetDailyRosterAsync(int? dayOffset = null, RosterDisclosure? disclosure = null, CancellationToken ct = default);
}

/// <summary>
/// Who is taking personal rows off the page and how: a CSV download (<see cref="AuditAction.CantinaRosterExported"/>)
/// or the allergy and intolerance reveal (<see cref="AuditAction.CantinaHealthDetailsRevealed"/>). Plain page reads
/// pass none and are not audited.
/// </summary>
internal sealed record RosterDisclosure(Guid ActorUserId, AuditAction Action)
{
    public static RosterDisclosure Export(Guid actorUserId) => new(actorUserId, AuditAction.CantinaRosterExported);
    public static RosterDisclosure HealthReveal(Guid actorUserId) => new(actorUserId, AuditAction.CantinaHealthDetailsRevealed);

    internal string Verb => Action == AuditAction.CantinaRosterExported ? "exported" : "allergy and intolerance details revealed";
}
