using System.Buffers.Text;
using System.Security.Cryptography;
using Humans.Audit.Contracts;
using Humans.Auth.Contracts;
using Humans.Backdoor.Data;
using Humans.Backdoor.Domain;
using Humans.Base.Extensions;
using Humans.Base.Constants;
using Humans.Gdpr.Contracts;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Backdoor.Services;

internal sealed class BackdoorApiKeyService(
    IBackdoorApiKeyRepository repository,
    ISystemRoleService roles,
    IPermissionService permissions,
    IUserService users,
    IAuditLogService audit,
    IClock clock,
    ILogger<BackdoorApiKeyService> logger) : IBackdoorApiKeyService, IUserDataContributor, IUserMerge
{
    internal const string BackdoorApiKeys = "BackdoorApiKeys";

    /// <summary>Human-readable marker so a leaked key is recognisable in a log or a paste.</summary>
    private const string KeyPrefix = "hmn_";

    /// <summary>Bytes of entropy behind each key — 256 bits, base64url-encoded to 43 chars.</summary>
    private const int KeyEntropyBytes = 32;

    /// <summary>Leading characters kept in the clear so a human can tell their rows apart.</summary>
    private const int DisplayPrefixLength = 12;

    /// <summary>Matches the <c>varchar(100)</c> column, so an over-long label is a validation message, not an insert
    /// error.</summary>
    private const int MaxLabelLength = 100;

    /// <summary>Owners below Admin and Board get keys that lapse after this, so a forgotten key does not linger.</summary>
    internal static readonly Duration ShortKeyLifetime = Duration.FromDays(30);

    public async Task<BackdoorKeyCreateResult> CreateAsync(
        Guid ownerUserId, string label, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return BackdoorKeyCreateResult.Failed(BackdoorKeyError.LabelRequired);

        label = label.Trim();
        if (label.Length > MaxLabelLength)
            return BackdoorKeyCreateResult.Failed(BackdoorKeyError.LabelTooLong);

        var owner = await GetEligibleOwnerAsync(ownerUserId, ct);
        if (owner is null)
        {
            logger.LogWarning(
                "Backdoor key creation refused for {OwnerUserId}: no Backdoor scope admits them", ownerUserId);
            return BackdoorKeyCreateResult.Failed(BackdoorKeyError.NotEligible);
        }

        var plaintext = NewPlaintext();
        var key = NewKey(ownerUserId, label, actorUserId, plaintext, await ExpiryForAsync(owner, ct));
        await repository.AddAsync(key, ct);
        await AuditAsync(BackdoorAuditActions.BackdoorApiKeyCreated, key, actorUserId, "created");
        return BackdoorKeyCreateResult.Success(plaintext);
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
            await AuditAsync(BackdoorAuditActions.BackdoorApiKeyRevoked, key, actorUserId, "revoked");

        return revoked;
    }

    public async Task<BackdoorKeyCreateResult> RotateAsync(
        Guid keyId, Guid actorUserId, CancellationToken ct = default)
    {
        var key = await repository.GetByIdAsync(keyId, ct);
        if (key is null || !key.IsActive)
        {
            logger.LogWarning("Backdoor key {KeyId} not rotatable: missing or already revoked", keyId);
            return BackdoorKeyCreateResult.Failed(BackdoorKeyError.NotFound);
        }

        // Eligibility is re-checked on rotation, not just at creation: an owner who has since lost every scope, or
        // been suspended, does not get a fresh credential out of a rotate. The expiry is worked out afresh too.
        var owner = await GetEligibleOwnerAsync(key.UserId, ct);
        if (owner is null)
            return BackdoorKeyCreateResult.Failed(BackdoorKeyError.NotEligible);

        var plaintext = NewPlaintext();
        var replacement = NewKey(key.UserId, key.Label, actorUserId, plaintext, await ExpiryForAsync(owner, ct));
        if (!await repository.RotateAsync(keyId, actorUserId, clock.GetCurrentInstant(), replacement, ct))
            return BackdoorKeyCreateResult.Failed(BackdoorKeyError.NotFound);

        await AuditAsync(BackdoorAuditActions.BackdoorApiKeyRevoked, key, actorUserId, "rotated out");
        await AuditAsync(BackdoorAuditActions.BackdoorApiKeyCreated, replacement, actorUserId, "created");
        return BackdoorKeyCreateResult.Success(plaintext);
    }

    public async Task<IReadOnlyList<BackdoorKeyRow>> ListAsync(CancellationToken ct = default)
    {
        var keys = await repository.GetAllAsync(ct);
        var owners = new Dictionary<Guid, (bool Eligible, bool AdminOrBoard)>();
        foreach (var ownerId in keys.Select(k => k.UserId).Distinct())
        {
            var owner = await GetEligibleOwnerAsync(ownerId, ct);
            owners[ownerId] = (owner is not null, owner is not null && await IsAdminOrBoardAsync(owner, ct));
        }

        var now = clock.GetCurrentInstant();
        return [.. keys.Select(k =>
        {
            var (eligible, adminOrBoard) = owners[k.UserId];
            var expiresAt = EffectiveExpiry(k, adminOrBoard);
            return new BackdoorKeyRow(
                k.Id, k.UserId, k.Label, k.DisplayPrefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt, expiresAt,
                eligible, expiresAt <= now);
        })];
    }

    public async Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct = default) =>
        await GetEligibleOwnerAsync(userId, ct) is not null;

    /// <summary>
    /// Refused, not auto-revoked: a role can expire or be revoked and an account suspended, but a restored role
    /// restores the key, and a transient gap must not destroy a credential.
    /// </summary>
    public async Task<BackdoorKeyOwner?> ResolveOwnerAsync(string presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(presentedKey)) return null;

        var key = await repository.FindActiveByHashAsync(Hash(presentedKey), ct);
        if (key is null) return null;

        var owner = await GetEligibleOwnerAsync(key.UserId, ct);
        if (owner is null)
        {
            logger.LogWarning(
                "Backdoor key {KeyId} refused: no Backdoor scope admits owner {OwnerUserId} any more",
                key.Id, key.UserId);
            return null;
        }

        var now = clock.GetCurrentInstant();
        var expiresAt = EffectiveExpiry(key, await IsAdminOrBoardAsync(owner, ct));
        if (expiresAt <= now)
        {
            logger.LogWarning("Backdoor key {KeyId} refused: expired at {ExpiresAt}", key.Id, expiresAt);
            return null;
        }

        await repository.TouchAsync(key.Id, now, ct);
        return owner;
    }

    public async Task<bool> AdmitsAsync(BackdoorKeyOwner owner, BackdoorScope scope, CancellationToken ct = default) =>
        scope switch
        {
            BackdoorScope.AdminAndBoard => owner.Roles.Any(a => a.RoleName is RoleNames.Admin or RoleNames.Board),
            BackdoorScope.Tasks => (await permissions.GetOwnersAsync(owner.UserId, TasksPermissions.Manage, ct)).Count > 0,
            _ => false,
        };

    /// <summary>
    /// The state check is not redundant: suspension moves <c>users.State</c> and leaves role assignments standing, so a
    /// role-only test would keep a suspended owner's key working.
    /// </summary>
    private async Task<BackdoorKeyOwner?> GetEligibleOwnerAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetUserInfoAsync(userId, ct);
        if (user is null || user.State != UserState.Active) return null;

        var owner = new BackdoorKeyOwner(userId, await roles.GetActiveForUserAsync(userId, ct));
        foreach (var scope in Enum.GetValues<BackdoorScope>())
        {
            if (await AdmitsAsync(owner, scope, ct))
                return owner;
        }

        return null;
    }

    private async Task<Instant?> ExpiryForAsync(BackdoorKeyOwner owner, CancellationToken ct) =>
        await IsAdminOrBoardAsync(owner, ct) ? null : clock.GetCurrentInstant() + ShortKeyLifetime;

    private Task<bool> IsAdminOrBoardAsync(BackdoorKeyOwner owner, CancellationToken ct) =>
        AdmitsAsync(owner, BackdoorScope.AdminAndBoard, ct);

    /// <summary>
    /// A never-expiring key was made while its owner was Admin or Board. Once they no longer are, it lapses as if it had
    /// been made short-lived, so leaving the Board does not leave a permanent key behind.
    /// </summary>
    private static Instant? EffectiveExpiry(BackdoorApiKey key, bool ownerIsAdminOrBoard) =>
        key.ExpiresAt ?? (ownerIsAdminOrBoard ? null : key.CreatedAt + ShortKeyLifetime);

    private BackdoorApiKey NewKey(
        Guid ownerUserId, string label, Guid actorUserId, string plaintext, Instant? expiresAt) => new()
        {
            Id = Guid.NewGuid(),
            UserId = ownerUserId,
            KeyHash = Hash(plaintext),
            DisplayPrefix = plaintext[..DisplayPrefixLength],
            Label = label,
            CreatedAt = clock.GetCurrentInstant(),
            CreatedByUserId = actorUserId,
            ExpiresAt = expiresAt,
        };

    private Task AuditAsync(AuditActionDefinition action, BackdoorApiKey key, Guid actorUserId, string verb) =>
        audit.LogAsync(
            action,
            BackdoorAuditSubjectTypes.BackdoorApiKey,
            key.Id,
            $"Backdoor API key '{key.Label}' ({key.DisplayPrefix}…) {verb}",
            actorUserId,
            targetId: key.UserId,
            targetType: AuditSubjectTypes.User);

    private static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext)));

    private static string NewPlaintext() =>
        KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyEntropyBytes));

    // The hash is never exported: it is the credential itself.

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
                ExpiresAt = k.ExpiresAt.ToIso8601(),
            })
            .ToList();

        return [new UserDataSlice(BackdoorApiKeys, shaped.Count == 0 ? null : shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BackdoorApiKeys] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.EraseForUserAsync(userId, ct);

    /// <summary>A key whose new owner no scope admits stops authenticating: <see cref="ResolveOwnerAsync"/> re-checks.</summary>
    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, ct);
}
