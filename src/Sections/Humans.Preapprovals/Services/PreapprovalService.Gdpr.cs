using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>
/// The GDPR surface. Export: the person's own grants, and what they did as a creator, manager
/// or decider. Erasure deletes what was never decided (Applied rows, manager rows); every
/// decided grant is a commitment of the association's money and is kept under Art. 17(3)(b).
/// Merge re-keys every user-id column onto the surviving account.
/// </summary>
internal sealed partial class PreapprovalService : IUserDataContributor, IUserMerge
{
    internal const string Preapprovals = "Preapprovals";
    internal const string PreapprovalDecisions = "PreapprovalDecisions";

    private const string FiscalRetention =
        "Retained in full except pending applications and manager rows, which are deleted: a " +
        "decided grant (issued, approved, declined, revoked, claimed or consumed) is the " +
        "association's record of money it committed against its budget, kept with the " +
        "beneficiary's id, the amount, the label, the application note and the deciding " +
        "manager. Spanish law requires the books and their supporting records be kept 6 years " +
        "(Código de Comercio Art. 30) and 4 years for tax purposes (Ley 58/2003 Art. 66). " +
        "GDPR Art. 17(3)(b).";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Preapprovals] = FiscalRetention,
            [PreapprovalDecisions] = FiscalRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await repository.GetRowsForUsersAsync([userId], ct);
        var programs = (await repository.GetProgramsAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
        string NameOf(Guid programId) => programs.TryGetValue(programId, out var name) ? name : "(unknown)";

        var grants = rows.Grants.Select(g => new
        {
            Program = NameOf(g.ProgramId),
            g.Label,
            g.MaxAmount,
            Status = g.Status.ToString(),
            g.ApplicationNote,
            g.RequestedAmount,
            DecidedAt = g.DecidedAt.ToIso8601(),
            g.ExpenseReportId,
            CreatedAt = g.CreatedAt.ToIso8601()
        }).ToList();

        var decisions = new
        {
            ProgramsCreated = rows.ProgramsCreated.Select(p => new { p.Name, CreatedAt = p.CreatedAt.ToIso8601() }).ToList(),
            ManagerOf = rows.ManagerRows.Select(m => new { Program = NameOf(m.ProgramId), AddedAt = m.AddedAt.ToIso8601() }).ToList(),
            ManagersAdded = rows.ManagersAdded.Select(m => new { Program = NameOf(m.ProgramId), AddedAt = m.AddedAt.ToIso8601() }).ToList(),
            GrantsDecided = rows.GrantsDecided.Select(g => new
            {
                Program = NameOf(g.ProgramId),
                g.MaxAmount,
                Status = g.Status.ToString(),
                DecidedAt = g.DecidedAt.ToIso8601()
            }).ToList()
        };
        var hasDecisions = decisions.ProgramsCreated.Count + decisions.ManagerOf.Count
            + decisions.ManagersAdded.Count + decisions.GrantsDecided.Count > 0;

        return
        [
            new UserDataSlice(Preapprovals, grants),
            new UserDataSlice(PreapprovalDecisions, hasDecisions ? decisions : null)
        ];
    }

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repository.EraseUserAsync(userId, ct);

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var dropped = await repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
        foreach (var grantId in dropped)
        {
            await ResolveRequestAsync(grantId, actorUserId, ct);
            await auditLog.LogAsync(AuditAction.PreapprovalMergeDroppedApplication, AuditEntityTypes.Grant, grantId,
                $"Duplicate pending application dropped on merge of {mergedFromUserId} into {mergedToUserId}.",
                actorUserId, relatedEntityId: mergedToUserId, relatedEntityType: AuditEntityTypes.User);
        }
    }
}
