using Humans.Base.Interfaces;

namespace Humans.Grants.Contracts;

/// <summary>
/// The one write Grants exposes: a source section (Low Income) filling a program it was given.
/// A program whose issuer is a source names the section key allowed to fill it; any other
/// caller, a closed program, or a managers' program is refused with
/// <see cref="InvalidOperationException"/>. Programs, managers, manual issue, revoke and resend
/// stay internal. Every call is audited with the actor and the source's own reference.
/// </summary>
public interface IGrantWrite : IApplicationService
{
    /// <summary>
    /// Entitles <paramref name="userId"/> under <paramref name="programId"/> and tells them, by
    /// email and in-app. Idempotent per person and program: when they already hold a live
    /// grant there, its id is returned and nothing is sent again.
    /// </summary>
    /// <remarks>
    /// <c>sourceKey</c> is the calling section's key, matched against the program's;
    /// <c>sourceRef</c> is the caller's own id for this decision, stored on the grant.
    /// </remarks>
    Task<Guid> IssueAsync(
        Guid programId, string sourceKey, Guid userId, string sourceRef, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes the person's live grant under <paramref name="programId"/> and tells them, with
    /// <paramref name="reason"/>. No-op when they hold none.
    /// </summary>
    Task RevokeAsync(
        Guid programId, string sourceKey, Guid userId, string? reason, Guid actorUserId,
        CancellationToken ct = default);
}
