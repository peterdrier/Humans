using Humans.Base.Interfaces;
using Humans.Grants.Contracts;
using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>Authorization is the controllers' job: every method trusts its actor.</summary>
internal interface IGrantManagement : IGrantWrite, IApplicationService
{
    Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default);
    Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default);

    /// <summary>Every grant of the person, any state, across every id they have held.</summary>
    Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default);

    Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Europe/Madrid, the date expiry is checked against.</summary>
    LocalDate Today { get; }

    /// <summary>Every program without an event id whose year has an event, and the event <c>grants.event-ids</c> gives it.</summary>
    Task<IReadOnlyList<(Guid ProgramId, string ProgramName, int Year, EventSettingsInfo Event)>> GetEventIdPlanAsync(
        CancellationToken ct = default);

    /// <summary>Applies <see cref="GetEventIdPlanAsync"/>; returns the rows written. Idempotent.</summary>
    Task<int> AssignProgramEventIdsAsync(CancellationToken ct = default);

    /// <summary>The program's year follows the chosen event, which must exist and not be deleted.</summary>
    Task<GrantMutationResult<Guid>> CreateProgramAsync(ProgramSave save, Guid actorUserId, CancellationToken ct = default);
    Task<GrantMutationResult> UpdateProgramAsync(Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// People already holding a live grant in the program are skipped; the result carries the ids created.
    /// <paramref name="expiresOn"/> overrides the program's default expiry.
    /// </summary>
    Task<GrantMutationResult<IReadOnlyList<Guid>>> IssueToAsync(
        Guid programId, IReadOnlyList<Guid> userIds, LocalDate? expiresOn, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>The optional reason is shown to the person.</summary>
    Task<GrantMutationResult> RevokeGrantAsync(Guid grantId, string? reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Live grants only.</summary>
    Task<GrantMutationResult> ResendNoticeAsync(Guid grantId, Guid actorUserId, CancellationToken ct = default);
}
