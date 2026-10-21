using Humans.Base.Interfaces;

namespace Humans.Grants.Contracts;

/// <summary>
/// The one write Grants exposes: a source section filling the program that names its key.
/// The caller passes only its source key; Grants resolves the one open program whose issuer is
/// that source (at most one is open per key). No such program, or any other refusal, is an
/// <see cref="InvalidOperationException"/>. Programs, managers, manual issue, revoke and resend
/// stay internal. Every call is audited with the actor and the source's own reference.
/// </summary>
public interface IGrantWrite : IApplicationService
{
    /// <summary>
    /// Entitles <paramref name="userId"/> under the open program filled by
    /// <paramref name="sourceKey"/> and tells them, by email and in-app. Idempotent per person:
    /// when they already hold a live grant there, its id is returned and nothing is sent again.
    /// <paramref name="sourceRef"/> is the caller's own id for this decision, stored on the grant.
    /// </summary>
    Task<Guid> IssueAsync(
        string sourceKey, Guid userId, string? sourceRef, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Revokes the person's live grant under the open program filled by
    /// <paramref name="sourceKey"/> and tells them, with <paramref name="reason"/>. Returns false
    /// when they hold none.
    /// </summary>
    Task<bool> RevokeAsync(
        string sourceKey, Guid userId, string? reason, Guid actorUserId, CancellationToken ct = default);
}
