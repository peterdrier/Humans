using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// The Google Workspace identity GoogleIntegration owns (<c>google_workspace_identities</c>, one
/// row per member): the reads other sections make, the designation, sync verdicts, the
/// Article 15/17 slice and the account-merge fold.
/// </summary>
/// <remarks>
/// The identity is resolved against the member's verified addresses on every read, which replaces
/// Users' old <c>EnsureGoogleInvariantAsync</c> repair on every email write: a verified
/// <c>@nobodies.team</c> address wins (keeping the designated one if it is such an address), else
/// the designated address while it is still verified, else the verified primary, else the first
/// verified address. For data that satisfied the old invariant (exactly one verified IsGoogle
/// row, a @nobodies.team one whenever the member has one) this is that same row.
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

    public async Task ClearIfAddressAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var id = info?.Id ?? userId;
        var designated = (await repo.GetAsync(id, ct))?.Email;
        if (designated is null || !Same(designated, email)) return;

        await repo.DeleteAsync(id, ct);
    }

    // ─── Resolution ──────────────────────────────────────────────────────────────

    /// <summary>The effective identity for <paramref name="info"/> given its row (null = nothing
    /// designated). Null when the member has no verified address.</summary>
    internal static WorkspaceIdentity? Resolve(UserInfo info, WorkspaceIdentityRecord? row)
    {
        // UserEmails is ordered primary first, then by address.
        var verified = info.UserEmails.Where(e => e.IsVerified).ToList();
        if (verified.Count == 0) return null;

        var designated = row?.Email;
        var designatedRow = designated is null ? null : verified.FirstOrDefault(e => Same(e.Email, designated));
        var nobodies = verified.FirstOrDefault(e => IsNobodies(e.Email));

        var chosen = designatedRow is not null && (nobodies is null || IsNobodies(designatedRow.Email))
            ? designatedRow
            : nobodies
                ?? verified.FirstOrDefault(e => e.IsPrimary)
                ?? verified.OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase).First();

        var status = row?.Email is not null && Same(row.Email, chosen.Email) ? row.Status : GoogleEmailStatus.Unknown;
        return new WorkspaceIdentity(info.Id, chosen.Email, status);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsNobodies(string email) => email.EndsWith(NobodiesTeamSuffix, StringComparison.OrdinalIgnoreCase);

    // ─── IUserMerge ──────────────────────────────────────────────────────────────

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

    // ─── IUserDataContributor ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var identity = await GetWorkspaceIdentityAsync(userId, ct);
        return [new UserDataSlice(WorkspaceIdentitySlice,
            identity is null ? null : new { identity.Email, Status = identity.Status.ToString() })];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkspaceIdentitySlice] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Deletes the member's row. The frozen legacy <c>user_emails</c> flags go with the
    /// address rows Users erases.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        await repo.DeleteAsync(userId, ct);
}
