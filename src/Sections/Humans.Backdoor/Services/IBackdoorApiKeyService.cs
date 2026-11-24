using Humans.Base.Interfaces;
using Humans.Auth.Contracts;
using NodaTime;

namespace Humans.Backdoor.Services;

internal interface IBackdoorApiKeyService : IApplicationService
{
    /// <summary>
    /// Returns the plaintext: the only moment it exists. Rejects a missing or over-long label and an owner no <see cref="BackdoorScope"/> admits. Owners below Admin and Board get
    /// a key that expires after <see cref="BackdoorApiKeyService.ShortKeyLifetime"/>.
    /// </summary>
    Task<BackdoorKeyCreateResult> CreateAsync(
        Guid ownerUserId, string label, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Revokes an active key. Null on success, else <see cref="BackdoorKeyError.NotFound"/> or <see cref="BackdoorKeyError.AlreadyRevoked"/>.</summary>
    Task<BackdoorKeyError?> RevokeAsync(Guid keyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the key with one for the same owner and label, its expiry worked out afresh. Fails as
    /// <see cref="RevokeAsync"/> does, and when its owner no longer qualifies.
    /// </summary>
    Task<BackdoorKeyCreateResult> RotateAsync(
        Guid keyId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Includes revoked keys, each flagged with whether its owner passes <see cref="ResolveOwnerAsync"/>'s
    /// eligibility test. Unordered.</summary>
    Task<IReadOnlyList<BackdoorKeyRow>> ListAsync(CancellationToken ct = default);

    /// <summary>Whether some <see cref="BackdoorScope"/> admits this active account, so it may hold a key.</summary>
    Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Null when the key is unknown, revoked, expired, or its owner no longer qualifies. Stamps last-used only when it
    /// resolves.
    /// </summary>
    Task<BackdoorKeyOwner?> ResolveOwnerAsync(string presentedKey, CancellationToken ct = default);

    /// <summary>Whether the scope admits the key's owner, so a controller declaring it serves them.</summary>
    Task<bool> AdmitsAsync(BackdoorKeyOwner owner, BackdoorScope scope, CancellationToken ct = default);
}

[method: System.Text.Json.Serialization.JsonConstructor]
internal sealed record BackdoorKeyOwner(Guid UserId, IReadOnlyList<RoleAssignmentSnapshot> Roles);

internal enum BackdoorKeyError
{
    LabelRequired,
    LabelTooLong,
    NotEligible,
    NotFound,
    AlreadyRevoked,
    NotAllowed,
}

/// <summary><see cref="PlaintextKey"/> is non-null only on success and never retrievable again.</summary>
internal sealed record BackdoorKeyCreateResult(bool Succeeded, string? PlaintextKey, BackdoorKeyError? Error)
{
    public static BackdoorKeyCreateResult Success(string plaintext) => new(true, plaintext, null);

    public static BackdoorKeyCreateResult Failed(BackdoorKeyError error) => new(false, null, error);
}

/// <summary>One row of the key list. Owner display data is stitched by the controller.</summary>
internal sealed record BackdoorKeyRow(
    Guid Id,
    Guid UserId,
    string Label,
    string DisplayPrefix,
    Instant CreatedAt,
    Instant? LastUsedAt,
    Instant? RevokedAt,
    Instant? ExpiresAt,
    bool IsOwnerEligible,
    bool IsExpired);
