using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>The ceiling is checked on every path that commits money (issue, approve, raise) and nowhere else.</summary>
internal sealed partial class PreapprovalService
{
    private const int LabelMax = 200;
    private const int NoteMax = 2000;

    public async Task<PreapprovalMutationResult<Guid>> IssueAsync(
        Guid programId, Guid beneficiaryUserId, decimal? amount, string? label, Guid actorUserId,
        CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        if (program is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if ((label?.Length ?? 0) > LabelMax) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.TextTooLong));
        if (CanCommit(program) is { } closed) return new(Refusal: closed);

        var sized = SizeGrant(program, amount);
        if (sized.Refusal is not null) return new(Refusal: sized.Refusal);
        if (CeilingCheck(program, sized.Value) is { } over) return new(Refusal: over);

        var now = clock.GetCurrentInstant();
        var grant = NewGrant(program, beneficiaryUserId, sized.Value, Trimmed(label), GrantStatus.Open, now);
        grant.DecidedByUserId = actorUserId;
        grant.DecidedAt = now;
        await PersistIssuedGrantsAsync(program, [grant], actorUserId,
            issued => $"Issued {Euros(issued.MaxAmount)} under \"{program.Name}\"{LabelSuffix(issued)} to {beneficiaryUserId}; claim by {program.ClaimBy.ToInvariantDate()}.", ct);
        return new(grant.Id);
    }

    public async Task<PreapprovalMutationResult<IReadOnlyList<Guid>>> IssueManyAsync(
        Guid programId, IReadOnlyList<Guid> beneficiaryUserIds, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(beneficiaryUserIds);
        var program = await repository.GetProgramAsync(programId, ct);
        if (program is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (program.Kind != ProgramKind.FixedPerPerson)
            return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.IssueManyFixedOnly));
        if (CanCommit(program) is { } closed) return new(Refusal: closed);

        var ids = beneficiaryUserIds.Distinct().ToList();
        if (ids.Count == 0) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.NoBeneficiaries));

        var each = program.PerPersonAmount!.Value;
        if (MoneyRefusal(each) is { } amountRefusal) return new(Refusal: amountRefusal);
        if (CeilingCheck(program, each * ids.Count) is { } over) return new(Refusal: over);

        var now = clock.GetCurrentInstant();
        var grants = ids.Select(id =>
        {
            var g = NewGrant(program, id, each, null, GrantStatus.Open, now);
            g.DecidedByUserId = actorUserId;
            g.DecidedAt = now;
            return g;
        }).ToList();
        await PersistIssuedGrantsAsync(program, grants, actorUserId,
            grant => $"Issued {Euros(grant.MaxAmount)} under \"{program.Name}\" to {grant.BeneficiaryUserId} (batch of {grants.Count}); claim by {program.ClaimBy.ToInvariantDate()}.", ct);
        return new(grants.Select(g => g.Id).ToList());
    }

    private async Task PersistIssuedGrantsAsync(
        PreapprovalProgram program, IReadOnlyList<PreapprovalGrant> grants, Guid actorUserId,
        Func<PreapprovalGrant, string> describe, CancellationToken ct)
    {
        await repository.AddGrantsAsync(grants, ct);
        // The grants committed; finish their audit and notices after the browser disconnects.
        foreach (var grant in grants)
        {
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantIssued, grant, describe(grant), actorUserId);
            await AnnounceIssuedAsync(ToInfo(grant, program), CancellationToken.None);
        }
    }

    public async Task<PreapprovalMutationResult<Guid>> ApplyAsync(
        Guid programId, Guid applicantUserId, string? note, decimal? requestedAmount, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        if (program is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if ((note?.Length ?? 0) > NoteMax) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.TextTooLong));

        var info = ToInfo(program);
        if (!info.IsAcceptingApplications(Today))
            return new(Refusal: new PreapprovalRefusal(
                program.Status == ProgramStatus.Closed ? PreapprovalErrorKeys.ProgramClosed
                : !program.AcceptsApplications ? PreapprovalErrorKeys.NotAcceptingApplications
                : PreapprovalErrorKeys.DeadlinePassed));

        decimal amount;
        if (program.Kind == ProgramKind.FixedPerPerson)
        {
            amount = program.PerPersonAmount!.Value;
            requestedAmount = null;
        }
        else
        {
            if (requestedAmount is not { } requested) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.AmountRequired));
            amount = requested;
        }
        if (MoneyRefusal(amount) is { } amountRefusal) return new(Refusal: amountRefusal);

        var ids = await AllIdsAsync(applicantUserId, ct);
        var now = clock.GetCurrentInstant();
        var pending = program.Grants.FirstOrDefault(g => g.Status == GrantStatus.Applied && ids.Contains(g.BeneficiaryUserId));
        if (pending is not null)
        {
            // A second submit edits the pending application; the managers' prompt is re-sent so
            // the newest text is the one they see.
            pending.ApplicationNote = Trimmed(note);
            pending.RequestedAmount = requestedAmount;
            pending.MaxAmount = amount;
            pending.UpdatedAt = now;
            await repository.UpdateGrantAsync(pending, ct);
            // The grant committed; finish its audit and notices after the browser disconnects.
            ct = CancellationToken.None;
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantApplied, pending,
                $"Application to \"{program.Name}\" edited: {Euros(amount)}.", applicantUserId);
            await ResolveRequestAsync(pending.Id, applicantUserId, ct);
            await AnnounceApplicationAsync(info, ToInfo(pending, program), ct);
            return new(pending.Id);
        }

        var grant = NewGrant(program, applicantUserId, amount, null, GrantStatus.Applied, now);
        grant.ApplicationNote = Trimmed(note);
        grant.RequestedAmount = requestedAmount;
        await repository.AddGrantsAsync([grant], ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;
        await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantApplied, grant,
            $"Applied to \"{program.Name}\": {Euros(amount)}.", applicantUserId);
        await AnnounceApplicationAsync(info, ToInfo(grant, program), ct);
        return new(grant.Id);
    }

    public async Task<PreapprovalMutationResult> ApproveAsync(
        Guid grantId, decimal? amount, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (grant.Status != GrantStatus.Applied) return new(new PreapprovalRefusal(PreapprovalErrorKeys.WrongStatus));
        var program = await repository.GetProgramAsync(grant.ProgramId, ct)
            ?? throw new InvalidOperationException($"Program {grant.ProgramId} of grant {grantId} is missing.");
        if (CanCommit(program) is { } closed) return new(closed);

        var sized = SizeGrant(program, amount ?? (program.Kind == ProgramKind.PerGrantAmount ? grant.RequestedAmount : null));
        if (sized.Refusal is not null) return new(sized.Refusal);
        if (CeilingCheck(program, sized.Value) is { } over) return new(over);

        var now = clock.GetCurrentInstant();
        grant.MaxAmount = sized.Value;
        grant.Status = GrantStatus.Open;
        grant.DecidedByUserId = actorUserId;
        grant.DecidedAt = now;
        grant.UpdatedAt = now;
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantApproved, grant,
            $"Application to \"{program.Name}\" approved: {Euros(grant.MaxAmount)}" +
            $"{(grant.RequestedAmount is { } r && r != grant.MaxAmount ? $" (requested {Euros(r)})" : "")}; claim by {program.ClaimBy.ToInvariantDate()}.",
            actorUserId);
        await ResolveRequestAsync(grant.Id, actorUserId, ct);
        await AnnounceIssuedAsync(ToInfo(grant, program), ct);
        return PreapprovalMutationResult.Ok;
    }

    public async Task<PreapprovalMutationResult> DeclineAsync(
        Guid grantId, string? note, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (grant.Status != GrantStatus.Applied) return new(new PreapprovalRefusal(PreapprovalErrorKeys.WrongStatus));
        if ((note?.Length ?? 0) > NoteMax) return new(new PreapprovalRefusal(PreapprovalErrorKeys.TextTooLong));

        var now = clock.GetCurrentInstant();
        grant.Status = GrantStatus.Declined;
        grant.DecidedByUserId = actorUserId;
        grant.DecidedAt = now;
        grant.UpdatedAt = now;
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        var reason = Trimmed(note);
        await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantDeclined, grant,
            $"Application to \"{grant.Program.Name}\" declined{(reason is null ? "" : $": {reason}")}.", actorUserId);
        await ResolveRequestAsync(grant.Id, actorUserId, ct);
        var info = ToInfo(grant);
        await NotifyDecidedAsync(info, "Preapprovals_Notice_Declined", reason, ct);
        await EmailAsync(info, PreapprovalNoticeKind.Declined, reason, ct);
        return PreapprovalMutationResult.Ok;
    }

    public async Task<PreapprovalMutationResult> RevokeAsync(
        Guid grantId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (grant.Status is not (GrantStatus.Open or GrantStatus.Claimed))
            return new(new PreapprovalRefusal(PreapprovalErrorKeys.WrongStatus));
        if ((reason?.Length ?? 0) > NoteMax) return new(new PreapprovalRefusal(PreapprovalErrorKeys.TextTooLong));

        // The amount and any report id stay: the row is the record of what was committed.
        grant.Status = GrantStatus.Revoked;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        var why = Trimmed(reason);
        await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantRevoked, grant,
            $"Revoked {Euros(grant.MaxAmount)} under \"{grant.Program.Name}\"{LabelSuffix(grant)}" +
            $"{(grant.ExpenseReportId is { } r ? $" while held by expense report {r}" : "")}{(why is null ? "" : $": {why}")}.",
            actorUserId);
        await ResolveFileByAsync(grant.Id, actorUserId, ct);
        var info = ToInfo(grant);
        await NotifyDecidedAsync(info, "Preapprovals_Notice_Revoked", why, ct);
        await EmailAsync(info, PreapprovalNoticeKind.Revoked, why, ct);
        return PreapprovalMutationResult.Ok;
    }

    public async Task<PreapprovalMutationResult> ChangeAmountAsync(
        Guid grantId, decimal amount, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (grant.Status is not (GrantStatus.Open or GrantStatus.Claimed))
            return new(new PreapprovalRefusal(PreapprovalErrorKeys.WrongStatus));
        if (MoneyRefusal(amount) is { } amountRefusal) return new(amountRefusal);
        if (amount == grant.MaxAmount) return PreapprovalMutationResult.Ok;

        var program = await repository.GetProgramAsync(grant.ProgramId, ct)
            ?? throw new InvalidOperationException($"Program {grant.ProgramId} of grant {grantId} is missing.");
        if (CeilingCheck(program, amount - grant.MaxAmount) is { } over) return new(over);

        var old = grant.MaxAmount;
        grant.MaxAmount = amount;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantAmountChanged, grant,
            $"Amount under \"{program.Name}\"{LabelSuffix(grant)} changed {Euros(old)} → {Euros(amount)}" +
            $"{(grant.ExpenseReportId is { } r ? $" (held by expense report {r}; its cap follows at next submit)" : "")}.",
            actorUserId);
        var info = ToInfo(grant, program);
        await NotifyDecidedAsync(info, "Preapprovals_Notice_AmountChanged", null, ct, previousAmount: old);
        await EmailAsync(info, PreapprovalNoticeKind.AmountChanged, null, ct, previousAmount: old);
        return PreapprovalMutationResult.Ok;
    }

    /// <summary>Issue and approve commit money: refused on a closed program or past its deadline.</summary>
    private PreapprovalRefusal? CanCommit(PreapprovalProgram program) =>
        program.Status == ProgramStatus.Closed ? new(PreapprovalErrorKeys.ProgramClosed)
        : Today > program.ClaimBy ? new(PreapprovalErrorKeys.DeadlinePassed)
        : null;

    private static (decimal Value, PreapprovalRefusal? Refusal) SizeGrant(PreapprovalProgram program, decimal? amount)
    {
        decimal value;
        if (program.Kind == ProgramKind.FixedPerPerson)
        {
            if (amount is not null) return (0, new(PreapprovalErrorKeys.AmountNotAllowed));
            value = program.PerPersonAmount!.Value;
        }
        else if (amount is { } supplied) value = supplied;
        else return (0, new(PreapprovalErrorKeys.AmountRequired));
        return MoneyRefusal(value) is { } refusal ? (0, refusal) : (value, null);
    }

    /// <summary>Granted + delta ≤ ceiling, else a refusal naming the headroom.</summary>
    private static PreapprovalRefusal? CeilingCheck(PreapprovalProgram program, decimal delta)
    {
        if (program.TotalCeiling is not { } ceiling) return null;
        var granted = program.Grants
            .Where(g => g.Status is GrantStatus.Open or GrantStatus.Claimed or GrantStatus.Consumed)
            .Sum(g => g.MaxAmount);
        return granted + delta > ceiling ? new(PreapprovalErrorKeys.CeilingExceeded, Euros(ceiling - granted)) : null;
    }

    private static PreapprovalGrant NewGrant(
        PreapprovalProgram program, Guid beneficiaryUserId, decimal amount, string? label, GrantStatus status, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProgramId = program.Id,
            Program = program,
            BeneficiaryUserId = beneficiaryUserId,
            Label = label,
            MaxAmount = amount,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static string LabelSuffix(PreapprovalGrant grant) =>
        grant.Label is null ? "" : $" ({grant.Label})";

    private async Task AnnounceIssuedAsync(GrantInfo info, CancellationToken ct)
    {
        await NotifyFileByAsync(info, "Preapprovals_Notice_Issued", NotificationPriority.Normal, ct);
        await EmailAsync(info, PreapprovalNoticeKind.Issued, detail: null, ct);
    }

    private async Task AnnounceApplicationAsync(ProgramInfo program, GrantInfo info, CancellationToken ct)
    {
        await SafeAsync(() => NotifyManagersAsync(program, info, ct), "notify managers", info.Id);
        await SafeAsync(() => EmailManagersAsync(program, info, ct), "email managers", info.Id);
    }
}
