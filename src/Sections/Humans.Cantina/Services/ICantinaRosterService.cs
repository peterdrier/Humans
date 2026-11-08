using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Cantina.Services.Dtos;

namespace Humans.Cantina.Services;

internal interface ICantinaRosterService : IApplicationService
{
    /// <summary>
    /// A null offset means the week containing today in the event's timezone (zero without an active event). With no
    /// event or nobody on site the DTO is still fully populated, with zero counts and empty lists.
    /// Returns null when the offset cannot represent the requested week or its offset arithmetic.
    /// </summary>
    // Disclosing callers (CSV download, allergy reveal) say who, so the service records the personal-data disclosure.
    Task<WeeklyRosterDto?> GetWeeklyRosterAsync(int? weekStartOffset = null, RosterDisclosure? disclosure = null, CancellationToken ct = default);

    /// <summary>
    /// A null offset means today in the event's timezone (zero without an active event). People are unsorted.
    /// Returns null when the offset cannot represent the requested day or its offset arithmetic.
    /// </summary>
    Task<DailyMatrixDto?> GetDailyRosterAsync(int? dayOffset = null, RosterDisclosure? disclosure = null, CancellationToken ct = default);
}

/// <summary>
/// Who is taking personal rows off the page and how: a CSV download (<see cref="CantinaAuditActions.CantinaRosterExported"/>)
/// or the allergy and intolerance reveal (<see cref="CantinaAuditActions.CantinaHealthDetailsRevealed"/>). Plain page reads
/// pass none and are not audited.
/// </summary>
internal sealed record RosterDisclosure(Guid ActorUserId, AuditActionDefinition Action)
{
    public static RosterDisclosure Export(Guid actorUserId) => new(actorUserId, CantinaAuditActions.CantinaRosterExported);
    public static RosterDisclosure HealthReveal(Guid actorUserId) => new(actorUserId, CantinaAuditActions.CantinaHealthDetailsRevealed);

    internal string Verb => Action == CantinaAuditActions.CantinaRosterExported ? "exported" : "allergy and intolerance details revealed";
}
