using Humans.Base.Interfaces;
using Humans.Auth.Contracts;
using NodaTime;

namespace Humans.Backdoor.Services;

internal interface IBackdoorApiKeyService : IApplicationService
{
    /// <summary>
    /// Returns the plaintext: the only moment it exists. Rejects a missing or over-long label, and an owner who is not
    /// a full Admin or Board member with an active account.
    /// </summary>
    Task<BackdoorKeyIssueResult> IssueAsync(
        Guid ownerUserId, string label, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Revokes an active key. Returns false if it is missing or already revoked.</summary>
    Task<bool> RevokeAsync(Guid keyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the key with one for the same owner and label. Fails when the key is missing, already revoked, or its
    /// owner no longer qualifies.
    /// </summary>
    Task<BackdoorKeyIssueResult> RotateAsync(
        Guid keyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Includes revoked keys, each flagged with whether its owner passes <see cref="ResolveOwnerAsync"/>'s
    /// eligibility test. Unordered.</summary>
    Task<IReadOnlyList<BackdoorKeyRow>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Null when the key is unknown, revoked, or its owner no longer qualifies. Stamps last-used only when it resolves.
    /// </summary>
    Task<BackdoorKeyOwner?> ResolveOwnerAsync(string presentedKey, CancellationToken ct = default);
}

[method: System.Text.Json.Serialization.JsonConstructor]
internal sealed record BackdoorKeyOwner(Guid UserId, IReadOnlyList<RoleAssignmentSnapshot> Roles);

/// <summary><see cref="PlaintextKey"/> is non-null only on success and never retrievable again.</summary>
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
    Instant? RevokedAt,
    bool IsOwnerEligible);
