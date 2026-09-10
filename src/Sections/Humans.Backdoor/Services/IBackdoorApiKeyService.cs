using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Backdoor.Services;

/// <summary>
/// Issue, revoke, rotate and validate the personal machine-API keys that gate
/// <c>/api/backdoor/*</c>. Section-internal: the admin controller and the auth filter are
/// the only callers, and nothing outside Backdoor has business with a key.
/// </summary>
internal interface IBackdoorApiKeyService : IApplicationService
{
    /// <summary>
    /// Issues a key to <paramref name="ownerUserId"/> and returns the plaintext. This is
    /// the only moment the plaintext exists — it is hashed on the way to the database and
    /// cannot be recovered. Rejects a missing or over-long label, and an owner who is not a
    /// full Admin or Board member with an active account.
    /// </summary>
    Task<BackdoorKeyIssueResult> IssueAsync(
        Guid ownerUserId, string label, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Revokes an active key. Returns false if it is missing or already revoked.</summary>
    Task<bool> RevokeAsync(Guid keyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Revokes <paramref name="keyId"/> and issues a replacement to the same owner with the
    /// same label. Returns the new plaintext, or a failure when the key is missing, already
    /// revoked, or its owner no longer qualifies.
    /// </summary>
    Task<BackdoorKeyIssueResult> RotateAsync(
        Guid keyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Every key, including revoked ones. Ordering is the caller's business.</summary>
    Task<IReadOnlyList<BackdoorKeyRow>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves a presented plaintext key to its owner. Returns null when the key is unknown,
    /// revoked, or its owner is no longer a full Admin or Board member with an active account;
    /// the last-used stamp is written only when it resolves.
    /// </summary>
    Task<Guid?> ResolveOwnerAsync(string presentedKey, CancellationToken ct = default);
}

/// <summary>
/// Outcome of an issue or rotate. <see cref="PlaintextKey"/> is non-null only on success and
/// is never retrievable again.
/// </summary>
internal sealed record BackdoorKeyIssueResult(bool Succeeded, string? PlaintextKey, string? Error)
{
    public static BackdoorKeyIssueResult Success(string plaintext) => new(true, plaintext, null);

    public static BackdoorKeyIssueResult Failed(string error) => new(false, null, error);
}

/// <summary>One row of the admin list. Owner display data is stitched by the controller.</summary>
internal sealed record BackdoorKeyRow(
    Guid Id,
    Guid UserId,
    string Label,
    string DisplayPrefix,
    Instant CreatedAt,
    Instant? LastUsedAt,
    Instant? RevokedAt);
