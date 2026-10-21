using Humans.Base.Interfaces;
using Humans.Grants.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>
/// The section's full surface: programs and their managers, issuing, revoking and resending,
/// over the one public face (<see cref="IGrantWrite"/>, a source section filling its program).
/// Internal. Authorization is the controllers' job: every method trusts its actor.
/// </summary>
internal interface IGrantManagement : IGrantWrite, IApplicationService
{
    // ── Reads ─────────────────────────────────────────────────────────────

    Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default);
    Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default);

    /// <summary>Every grant of the person, any state, across every id they have held.</summary>
    Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Nav and admin-index gate for people without an admin role.</summary>
    Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default);

    /// <summary>"Today" in Europe/Madrid, the date expiry is checked against.</summary>
    LocalDate Today { get; }

    // ── Programs: Admin and Board ─────────────────────────────────────────

    Task<GrantMutationResult<Guid>> CreateProgramAsync(ProgramSave save, Guid actorUserId, CancellationToken ct = default);
    Task<GrantMutationResult> UpdateProgramAsync(Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default);

    // ── Grants: a manager of the program or above ─────────────────────────

    /// <summary>
    /// Entitles each person in <paramref name="userIds"/>. People already holding a live grant
    /// in the program are skipped; the result carries the ids of the grants created.
    /// <paramref name="expiresOn"/> overrides the program's default expiry for these grants.
    /// </summary>
    Task<GrantMutationResult<IReadOnlyList<Guid>>> IssueToAsync(
        Guid programId, IReadOnlyList<Guid> userIds, LocalDate? expiresOn, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>Live → revoked, with an optional reason the person is told.</summary>
    Task<GrantMutationResult> RevokeGrantAsync(Guid grantId, string? reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Queues the email and in-app notice again for a live grant.</summary>
    Task<GrantMutationResult> ResendNoticeAsync(Guid grantId, Guid actorUserId, CancellationToken ct = default);
}
