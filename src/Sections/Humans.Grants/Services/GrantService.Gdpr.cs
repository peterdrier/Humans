using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>
/// The GDPR surface. Export: the person's own grants, and what they did as a creator, manager
/// or issuer. Erasure deletes the person's grants and manager rows; the bare actor ids on rows
/// they decided stay as the record of who decided. Merge re-keys every user-id column onto the
/// surviving account, revoking a duplicate live grant.
/// </summary>
internal sealed partial class GrantService : IUserDataContributor, IUserMerge
{
    internal const string Grants = "Grants";
    internal const string GrantDecisions = "GrantDecisions";

    private const string ActorRetention =
        "Manager rows are deleted. The person's id stays on programs they created and grants they " +
        "issued or revoked, as the record of who decided, which the Board relies on (GDPR Art. 6(1)(f)); " +
        "it is a bare id that no longer resolves to a person once the account is anonymized.";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Grants] = null,
            [GrantDecisions] = ActorRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await repository.GetRowsForUsersAsync(await AllIdsAsync(userId, ct), ct);

        var grants = rows.Grants.Select(g => new
        {
            Program = g.Program.Name,
            g.Program.Year,
            g.Program.Entitles,
            ExpiresOn = (g.ExpiresOn ?? g.Program.GrantsExpireOn)?.ToInvariantDate(),
            IssuedAt = g.IssuedAt.ToIso8601(),
            NotifiedAt = g.NotifiedAt.ToIso8601(),
            RevokedAt = g.RevokedAt.ToIso8601(),
            g.RevokeReason
        }).ToList();

        var decisions = new
        {
            ProgramsCreated = rows.ProgramsCreated.Select(p => new { p.Name, p.Year, CreatedAt = p.CreatedAt.ToIso8601() }).ToList(),
            ManagerOf = rows.ManagerRows.Select(m => new { Program = m.Program.Name, AddedAt = m.AddedAt.ToIso8601() }).ToList(),
            GrantsIssued = rows.GrantsIssued.Select(g => new { Program = g.Program.Name, IssuedAt = g.IssuedAt.ToIso8601() }).ToList(),
            GrantsRevoked = rows.GrantsRevoked.Select(g => new { Program = g.Program.Name, RevokedAt = g.RevokedAt.ToIso8601() }).ToList()
        };
        var hasDecisions = decisions.ProgramsCreated.Count + decisions.ManagerOf.Count
            + decisions.GrantsIssued.Count + decisions.GrantsRevoked.Count > 0;

        return
        [
            new UserDataSlice(Grants, grants),
            new UserDataSlice(GrantDecisions, hasDecisions ? decisions : null)
        ];
    }

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repository.EraseUserAsync(userId, ct);

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var revoked = await repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct);
        foreach (var grantId in revoked)
            await auditLog.LogAsync(AuditAction.GrantRevoked, AuditEntityTypes.Grant, grantId,
                $"Duplicate grant revoked on merge of {mergedFromUserId} into {mergedToUserId}.",
                actorUserId, relatedEntityId: mergedToUserId, relatedEntityType: AuditEntityTypes.User);
    }
}
