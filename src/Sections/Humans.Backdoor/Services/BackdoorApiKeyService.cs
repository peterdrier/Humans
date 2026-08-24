using System.Security.Cryptography;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Backdoor.Data;
using Humans.Backdoor.Domain;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Backdoor.Services;

internal sealed class BackdoorApiKeyService(
    IBackdoorApiKeyRepository repository,
    IRoleAssignmentService roles,
    IUserServiceRead users,
    IAuditLogService audit,
    IClock clock,
    ILogger<BackdoorApiKeyService> logger) : IBackdoorApiKeyService, IUserDataContributor, IUserMerge
{
    /// <summary>Human-readable marker so a leaked key is recognisable in a log or a paste.</summary>
    private const string KeyPrefix = "hmn_";

    /// <summary>Bytes of entropy behind each key — 256 bits, base64url-encoded to 43 chars.</summary>
    private const int KeyEntropyBytes = 32;

    /// <summary>Leading characters kept in the clear so a human can tell their rows apart.</summary>
    private const int DisplayPrefixLength = 12;

    /// <summary>Matches the <c>varchar(100)</c> Label column, so an over-long label is a
    /// validation message rather than a Postgres error from the insert.</summary>
    private const int MaxLabelLength = 100;

    public async Task<BackdoorKeyIssueResult> IssueAsync(
        Guid ownerUserId, string label, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return BackdoorKeyIssueResult.Failed("A label is required.");

        label = label.Trim();
        if (label.Length > MaxLabelLength)
            return BackdoorKeyIssueResult.Failed($"A label must be {MaxLabelLength} characters or fewer.");

        if (!await IsEligibleAsync(ownerUserId, ct))
        {
            logger.LogWarning(
                "Backdoor key issue refused for {OwnerUserId}: not an active Admin or Board member", ownerUserId);
            return BackdoorKeyIssueResult.Failed(
                "Keys can only be issued to a full Admin or a Board member with an active account.");
        }

        var plaintext = await PersistNewKeyAsync(ownerUserId, label, actorUserId, ct);
        return BackdoorKeyIssueResult.Success(plaintext);
    }

    public async Task<bool> RevokeAsync(Guid keyId, Guid actorUserId, CancellationToken ct = default)
    {
        var key = await repository.GetByIdAsync(keyId, ct);
        if (key is null || !key.IsActive)
        {
            logger.LogWarning("Backdoor key {KeyId} not revocable: missing or already revoked", keyId);
            return false;
        }

        var revoked = await repository.RevokeAsync(keyId, actorUserId, clock.GetCurrentInstant(), ct);
        if (revoked)
            await AuditAsync(AuditAction.BackdoorApiKeyRevoked, key, actorUserId, "revoked");

        return revoked;
    }

    public async Task<BackdoorKeyIssueResult> RotateAsync(
        Guid keyId, Guid actorUserId, CancellationToken ct = default)
    {
        var key = await repository.GetByIdAsync(keyId, ct);
        if (key is null || !key.IsActive)
        {
            logger.LogWarning("Backdoor key {KeyId} not rotatable: missing or already revoked", keyId);
            return BackdoorKeyIssueResult.Failed("That key no longer exists or is already revoked.");
        }

        // Eligibility is re-checked on rotation, not just at first issue: an owner who has
        // since lost Admin/Board, or been suspended, does not get a fresh credential out of a rotate.
        if (!await IsEligibleAsync(key.UserId, ct))
            return BackdoorKeyIssueResult.Failed(
                "The key's owner is no longer a full Admin or a Board member with an active account.");

        if (!await repository.RevokeAsync(keyId, actorUserId, clock.GetCurrentInstant(), ct))
            return BackdoorKeyIssueResult.Failed("That key no longer exists or is already revoked.");

        await AuditAsync(AuditAction.BackdoorApiKeyRevoked, key, actorUserId, "rotated out");

        var plaintext = await PersistNewKeyAsync(key.UserId, key.Label, actorUserId, ct);
        return BackdoorKeyIssueResult.Success(plaintext);
    }

    public async Task<IReadOnlyList<BackdoorKeyRow>> ListAsync(CancellationToken ct = default)
    {
        var keys = await repository.GetAllAsync(ct);
        return [.. keys.Select(k => new BackdoorKeyRow(
            k.Id, k.UserId, k.Label, k.DisplayPrefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt))];
    }

    /// <remarks>
    /// Eligibility is re-checked here, not just at issue time: an Admin or Board assignment
    /// can expire, be revoked, or be swept by <c>RevokeAllActiveAsync</c> when the account
    /// requests deletion, the account itself can be suspended, and a key that outlived
    /// either would otherwise keep working.
    /// Refusal is deliberate rather than auto-revocation — a restored role restores the key,
    /// and a transient gap must not destroy a credential.
    /// </remarks>
    public async Task<Guid?> ResolveOwnerAsync(string presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(presentedKey)) return null;

        var key = await repository.FindActiveByHashAsync(Hash(presentedKey), ct);
        if (key is null) return null;

        if (!await IsEligibleAsync(key.UserId, ct))
        {
            logger.LogWarning(
                "Backdoor key {KeyId} refused: owner {OwnerUserId} is no longer an active Admin or Board member",
                key.Id, key.UserId);
            return null;
        }

        await repository.TouchAsync(key.Id, clock.GetCurrentInstant(), ct);
        return key.UserId;
    }

    /// <summary>
    /// Who may hold a working key: a full Admin or a Board member whose account still has
    /// app access. The account-state half is not redundant with the role half — suspension
    /// (<c>HumanLifecycleService.SuspendAsync</c>) moves <c>users.State</c> and deliberately
    /// leaves role assignments standing, so a role-only test would keep authenticating a
    /// suspended admin's key while the rest of the app shows them the account-status wall.
    /// </summary>
    private async Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetUserInfoAsync(userId, ct);
        if (user is null || user.State != UserState.Active) return false;

        return await roles.IsUserAdminAsync(userId, ct) || await roles.IsUserBoardMemberAsync(userId, ct);
    }

    private async Task<string> PersistNewKeyAsync(
        Guid ownerUserId, string label, Guid actorUserId, CancellationToken ct)
    {
        var plaintext = KeyPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(KeyEntropyBytes));
        var key = new BackdoorApiKey
        {
            Id = Guid.NewGuid(),
            UserId = ownerUserId,
            KeyHash = Hash(plaintext),
            DisplayPrefix = plaintext[..DisplayPrefixLength],
            Label = label,
            CreatedAt = clock.GetCurrentInstant(),
            CreatedByUserId = actorUserId,
        };

        await repository.AddAsync(key, ct);
        await AuditAsync(AuditAction.BackdoorApiKeyIssued, key, actorUserId, "issued");
        return plaintext;
    }

    private Task AuditAsync(AuditAction action, BackdoorApiKey key, Guid actorUserId, string verb) =>
        audit.LogAsync(
            action,
            AuditEntityTypes.BackdoorApiKey,
            key.Id,
            $"Backdoor API key '{key.Label}' ({key.DisplayPrefix}…) {verb}",
            actorUserId,
            relatedEntityId: key.UserId,
            relatedEntityType: AuditEntityTypes.User);

    private static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── User-data fan-outs (design-rules §8a) ───────────────────────────────
    // backdoor_api_keys is user-keyed, so the section owes Article 15, Article 17 and the
    // account-merge fold. The hash is never exported: it is the credential itself.

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var keys = await repository.GetForUserAsync(userId, ct);

        var shaped = keys
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new
            {
                k.Label,
                k.DisplayPrefix,
                CreatedAt = k.CreatedAt.ToIso8601(),
                LastUsedAt = k.LastUsedAt.ToIso8601(),
                RevokedAt = k.RevokedAt.ToIso8601(),
            })
            .ToList();

        return [new UserDataSlice(GdprExportSections.BackdoorApiKeys, shaped.Count == 0 ? null : shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.BackdoorApiKeys] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// Keys the person owned are hard-deleted — a machine credential has no basis to outlive
    /// its owner — and they are detached as the creator or revoker of anyone else's key, so
    /// nothing of them survives on a row that is not theirs.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.EraseForUserAsync(userId, ct);

    /// <summary>
    /// Folds the eliminated account's keys onto the survivor. A key whose new owner holds
    /// neither role simply stops authenticating — <see cref="ResolveOwnerAsync"/> re-checks.
    /// </summary>
    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, ct);
}
