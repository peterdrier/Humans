using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Until <c>google.workspace-identities</c> has run, a member without a row reads the frozen legacy <c>user_emails</c>
/// flags through <see cref="UserInfo"/>.
/// </summary>
/// <remarks>
/// Resolved on every read: a verified <c>@nobodies.team</c> address wins, else the designated address while verified,
/// else the verified primary, else the first verified address.
/// </remarks>
internal sealed class WorkspaceIdentityService(
    IWorkspaceIdentityRepository repo,
    IUserService users,
    IAuditLogService audit,
    IClock clock) : IWorkspaceIdentityManagement, IUserDataContributor, IUserMerge
{
    internal const string WorkspaceIdentitySlice = "WorkspaceIdentity";
    internal const string NobodiesTeamSuffix = "@nobodies.team";

    public async Task<WorkspaceIdentity?> GetWorkspaceIdentityAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null) return null;
        return Resolve(info, await repo.GetAsync(info.Id, ct));
    }

    public async Task<IReadOnlyDictionary<Guid, WorkspaceIdentity>> GetWorkspaceIdentitiesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, WorkspaceIdentity>();
        var infos = await users.GetUserInfosAsync(userIds, ct);
        var rows = (await repo.GetAllAsync(ct)).ToDictionary(r => r.UserId);
        var result = new Dictionary<Guid, WorkspaceIdentity>();
        foreach (var (requestedId, info) in infos)
        {
            if (Resolve(info, rows.GetValueOrDefault(info.Id)) is { } identity)
                result[requestedId] = identity;
        }
        return result;
    }

    public async Task<bool> SetAsync(Guid userId, string email, Guid actorUserId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null || info.Id != userId) return false;

        var target = info.UserEmails.FirstOrDefault(e => e.IsVerified && Same(e.Email, email));
        if (target is null) return false;
        if (!IsNobodies(target.Email) && info.UserEmails.Any(e => e.IsVerified && IsNobodies(e.Email)))
            return false;

        var row = await repo.GetAsync(userId, ct);
        var current = Resolve(info, row);
        // Same address keeps its verdict, except a rejection: re-asserting it is the retry path.
        var status = current is not null && Same(current.Email, target.Email) && current.Status != GoogleEmailStatus.Rejected
            ? current.Status
            : GoogleEmailStatus.Unknown;
        await repo.UpsertAsync(userId, target.Email, status, clock.GetCurrentInstant(), ct);

        // Action, entity and wording unchanged from Users' SetGoogleAsync, so history reads back together.
        var previous = current is not null && !Same(current.Email, target.Email) ? current.Email : null;
        await audit.LogAsync(
            AuditAction.UserEmailGoogleSet,
            nameof(User), userId,
            previous is null
                ? $"Set Google identity to {target.Email}"
                : $"Set Google identity to {target.Email} (was {previous})",
            actorUserId,
            relatedEntityId: target.Id, relatedEntityType: nameof(UserEmail));
        return true;
    }

    public async Task<bool> SetStatusFromSyncAsync(
        Guid userId, string attemptedEmail, GoogleEmailStatus status, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null) return false;
        var current = Resolve(info, await repo.GetAsync(info.Id, ct));
        if (current is null || !Same(current.Email, attemptedEmail)) return false;

        // Rejected is terminal for an address: a later success must not flip it back to Valid.
        if (status == GoogleEmailStatus.Valid && current.Status == GoogleEmailStatus.Rejected) return false;
        if (current.Status == status) return false;

        await repo.UpsertAsync(info.Id, current.Email, status, clock.GetCurrentInstant(), ct);
        return true;
    }

    public async Task<bool> SetDriveEmailFromSyncAsync(
        Guid userId, string attemptedEmail, string driveEmail, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null) return false;
        var row = await repo.GetAsync(info.Id, ct);
        var current = Resolve(info, row);
        if (current is null || !Same(current.Email, attemptedEmail)) return false;
        if (row is { Email: not null, DriveEmail: not null } && Same(row.Email, current.Email) && Same(row.DriveEmail, driveEmail))
            return false;

        await repo.SetDriveEmailAsync(info.Id, current.Email, current.Status, driveEmail, clock.GetCurrentInstant(), ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<Guid, DriveEmailOverride>> GetDriveEmailsAsync(CancellationToken ct = default) =>
        (await repo.GetAllAsync(ct))
            .Where(r => r is { Email: not null, DriveEmail: not null })
            .ToDictionary(r => r.UserId, r => new DriveEmailOverride(r.Email!, r.DriveEmail!));

    public async Task ClearIfAddressAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var id = info?.Id ?? userId;
        var row = await repo.GetAsync(id, ct);
        var designated = row is not null ? row.Email : LegacyDesignation(info)?.Email;
        if (designated is null || !Same(designated, email)) return;

        // A cleared row, not a delete: the read must not fall back to the frozen legacy flag.
        await repo.UpsertAsync(id, null, GoogleEmailStatus.Unknown, clock.GetCurrentInstant(), ct);
    }

    public async Task<IReadOnlyList<LegacyWorkspaceIdentityRow>> GetUncopiedLegacyIdentitiesAsync(
        CancellationToken ct = default)
    {
        var copied = (await repo.GetAllAsync(ct)).Select(r => r.UserId).ToHashSet();
        return [.. (await users.GetAllUserInfosAsync(ct))
            .Where(u => !copied.Contains(u.Id) && HasLegacyState(u))
            .Select(u => (User: u, Identity: Resolve(u, null)))
            .Where(x => x.Identity is not null)
            .OrderBy(x => x.User.CreatedAt)
            .Select(x => new LegacyWorkspaceIdentityRow(
                x.User.Id, x.User.BurnerName, x.Identity!.Email, x.Identity.Status))];
    }

    public async Task<int> CopyLegacyIdentitiesAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var written = 0;
        foreach (var row in await GetUncopiedLegacyIdentitiesAsync(ct))
        {
            if (await repo.TryInsertAsync(row.UserId, row.Email, row.Status, now, ct))
                written++;
        }
        return written;
    }

    /// <summary>The effective identity for <paramref name="info"/> given its row (null = none yet,
    /// so the legacy flags stand in). Null when the member has no verified address.</summary>
    internal static WorkspaceIdentity? Resolve(UserInfo info, WorkspaceIdentityRecord? row)
    {
        // UserEmails is ordered primary first, then by address.
        var verified = info.UserEmails.Where(e => e.IsVerified).ToList();
        if (verified.Count == 0) return null;

        var designated = row is not null ? row.Email : LegacyDesignation(info)?.Email;
        var designatedRow = designated is null ? null : verified.FirstOrDefault(e => Same(e.Email, designated));
        var nobodies = verified.FirstOrDefault(e => IsNobodies(e.Email));

        var chosen = designatedRow is not null && (nobodies is null || IsNobodies(designatedRow.Email))
            ? designatedRow
            : nobodies
                ?? verified.FirstOrDefault(e => e.IsPrimary)
                ?? verified.OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase).First();

        var status = row is not null
            ? (row.Email is not null && Same(row.Email, chosen.Email) ? row.Status : GoogleEmailStatus.Unknown)
            : LegacyStatus(chosen);
        return new WorkspaceIdentity(info.Id, chosen.Email, status);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsNobodies(string email) => email.EndsWith(NobodiesTeamSuffix, StringComparison.OrdinalIgnoreCase);

    // The readers of the obsolete legacy fields: the read fallback and the copy above. Deleted with
    // the migration once production reports it done.
#pragma warning disable HUM_USER_GOOGLE_IDENTITY // the fallback and copy the fields are kept for (plan §3.1)
    private static UserEmailInfo? LegacyDesignation(UserInfo? info) =>
        info?.UserEmails.FirstOrDefault(e => e.IsGoogle && e.IsVerified);

    private static GoogleEmailStatus LegacyStatus(UserEmailInfo email) => (GoogleEmailStatus)(int)email.GoogleEmailStatus;

    private static bool HasLegacyState(UserInfo info) =>
        info.UserEmails.Any(e => e.IsVerified && (e.IsGoogle || e.GoogleEmailStatus != LegacyGoogleEmailStatus.Unknown));
#pragma warning restore HUM_USER_GOOGLE_IDENTITY

    /// <summary>The survivor keeps its own identity, as before (Users' merge clears the flag on the
    /// rows it moves); the archived account's row is deleted.</summary>
    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = mergedToUserId;
        _ = actorUserId;
        _ = now;
        await repo.DeleteAsync(mergedFromUserId, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var row = info is null ? null : await repo.GetAsync(info.Id, ct);
        var identity = info is null ? null : Resolve(info, row);
        var driveEmail = identity is not null && row is { Email: not null } && Same(row.Email, identity.Email)
            ? row.DriveEmail
            : null;
        return [new UserDataSlice(WorkspaceIdentitySlice,
            identity is null ? null : new { identity.Email, Status = identity.Status.ToString(), DriveEmail = driveEmail })];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkspaceIdentitySlice] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Deletes the member's row. The legacy <c>user_emails</c> flags go with the address
    /// rows Users erases.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        await repo.DeleteAsync(userId, ct);
}
