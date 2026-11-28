using Humans.Audit.Contracts;
using Humans.Base.Extensions;
using Humans.Finance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Users.Contracts;
using NodaTime;
using Humans.Base.Interfaces;
using System.Globalization;
using Humans.Base.Threading;
using Humans.Gdpr.Contracts;

namespace Humans.Preapprovals.Services;

/// <summary>
/// Every state change is a person's action (or Expenses' on their behalf). Deadline reminders have a separate owner.
/// </summary>
/// <remarks>
/// "Today" is the date in the org time zone, the zone the association books in. Every grant path that commits money
/// (issue, approve, raise) checks the ceiling.
/// </remarks>
internal sealed class PreapprovalService(
    IOrgTimeZone orgTimeZone,
    IPreapprovalRepository repository,
    IUserService users,
    IFinanceService finance,
    PreapprovalNotices notices,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<PreapprovalService> logger) : IPreapprovalManagement, IUserDataContributor, IUserMerge
{
    public async Task<PreapprovalWriteResult> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null)
            return new(Refusal: $"Pre-approval {grantId} does not exist.");

        if (grant.Status == GrantStatus.Claimed && grant.ExpenseReportId == expenseReportId)
        {
            // The holding report resubmitting after a rejection: filed in time, so no deadline check, but a fresh stamp.
            Defer(grant.Id, _ => AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Re-stamped for expense report {expenseReportId} (resubmit): cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId), afterCommit);
            return new(Claimed: ToOption(grant, grant.Program, Today));
        }

        var reason = grant.Status switch
        {
            GrantStatus.Revoked => "pre-approval revoked",
            GrantStatus.Claimed => "pre-approval already held by another expense report",
            GrantStatus.Consumed => "pre-approval already consumed",
            GrantStatus.Open when Today > grant.Program.ClaimBy =>
                $"claim deadline {grant.Program.ClaimBy.ToInvariantDate()} has passed",
            GrantStatus.Open => null,
            _ => $"pre-approval is {grant.Status}"
        };
        if (reason is not null)
            return new(Refusal: $"Pre-approval {grantId} cannot be claimed: {reason}.");

        grant.Status = GrantStatus.Claimed;
        grant.ExpenseReportId = expenseReportId;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, async completionToken =>
        {
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Claimed by expense report {expenseReportId}: cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId);
            await notices.ResolveFileByAsync(grant.Id, actorUserId, completionToken);
        }, afterCommit);
        return new(Claimed: ToOption(grant, grant.Program, Today));
    }

    public async Task<PreapprovalWriteResult> ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Open;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, async completionToken =>
        {
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantReleased, grant,
                $"Released by withdrawal of expense report {expenseReportId}; open again until {grant.Program.ClaimBy.ToInvariantDate()}.",
                actorUserId, expenseReportId);
            await notices.NotifyFileByAsync(ToInfo(grant), "Preapprovals_Notice_Released", NotificationPriority.Normal, completionToken);
        }, afterCommit);
        return new();
    }

    public async Task<PreapprovalWriteResult> ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Consumed;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, _ => AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantConsumed, grant,
            $"Consumed by approval of expense report {expenseReportId}: {Euros(grant.MaxAmount)}.",
            actorUserId, expenseReportId), afterCommit);
        return new();
    }

    private void Defer(Guid grantId, Func<CancellationToken, Task> completion,
        Action<Func<CancellationToken, Task>> afterCommit)
    {
        async Task CompleteAsync(CancellationToken token)
        {
            try { await completion(token); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to finish notices for committed pre-approval {GrantId}", grantId);
            }
        }

        afterCommit(CompleteAsync);
    }

    private async Task<(PreapprovalGrant? Grant, string? Refusal)> HeldByAsync(Guid grantId, Guid expenseReportId, CancellationToken ct)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return (null, $"Pre-approval {grantId} does not exist.");
        if (grant.Status == GrantStatus.Revoked)
            return (null, $"Pre-approval {grantId} cannot be used: pre-approval revoked.");
        if (grant.Status != GrantStatus.Claimed || grant.ExpenseReportId != expenseReportId)
            return (null, $"Pre-approval {grantId} is not held by expense report {expenseReportId} (status {grant.Status}).");
        return (grant, null);
    }

    internal static string Euros(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture) + " €";
    internal Task AuditGrantAsync(
        AuditActionDefinition action, PreapprovalGrant grant, string description, Guid actorUserId,
        Guid? expenseReportId = null) =>
        auditLog.LogAsync(action, PreapprovalsAuditSubjectTypes.Grant, grant.Id, description, actorUserId,
            targetId: expenseReportId ?? grant.ProgramId,
            targetType: expenseReportId is null ? PreapprovalsAuditSubjectTypes.Program : PreapprovalsAuditSubjectTypes.ExpenseReport);

    internal Task AuditProgramAsync(
        AuditActionDefinition action, PreapprovalProgram program, string description, Guid actorUserId,
        Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, PreapprovalsAuditSubjectTypes.Program, program.Id, description, actorUserId,
            targetId: relatedUserId,
            targetType: relatedUserId is null ? null : AuditSubjectTypes.User);

    private DateTimeZone OrgZone => orgTimeZone.Zone;

    internal LocalDate Today => clock.GetCurrentInstant().InZone(OrgZone).Date;

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        (await repository.GetProgramsAsync(ct)).Select(ToInfo).ToList();

    public async Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        return program is null ? null : ToInfo(program);
    }

    public async Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct)).Select(ToInfo).ToList();

    public async Task<IReadOnlyList<GrantOption>> GetGrantsAsync(CancellationToken ct)
    {
        var today = Today;
        return (await repository.GetProgramsAsync(ct))
            .SelectMany(p => p.Grants.Select(g => ToOption(g, p, today)))
            .ToList();
    }

    public async Task<bool> HasAnythingForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await AllIdsAsync(userId, ct);
        return await repository.HasGrantsForUsersAsync(ids, ct)
            || await repository.ManagesAnyProgramAsync(ids, ct)
            || await repository.AnyProgramAcceptsApplicationsAsync(Today, ct);
    }

    public async Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        await repository.ManagesAnyProgramAsync(await AllIdsAsync(userId, ct), ct);

    internal static ProgramInfo ToInfo(PreapprovalProgram p) => new(
        p.Id, p.Name, p.Description, p.Kind, p.PerPersonAmount, p.TotalCeiling, p.HoldedAccountNumber,
        p.AcceptsApplications, p.ClaimBy, p.Status,
        p.Managers.Select(m => m.UserId).ToList(),
        p.Grants.Select(g => ToInfo(g, p)).ToList());

    internal static GrantInfo ToInfo(PreapprovalGrant g) => ToInfo(g, g.Program);

    internal static GrantInfo ToInfo(PreapprovalGrant g, PreapprovalProgram p) => new(
        g.Id, g.ProgramId, p.Name, g.BeneficiaryUserId, g.Label, g.MaxAmount, g.Status,
        g.ApplicationNote, g.RequestedAmount, g.DecidedByUserId, g.DecidedAt, g.ExpenseReportId,
        p.ClaimBy, g.CreatedAt, g.UpdatedAt);

    internal static GrantOption ToOption(PreapprovalGrant g, PreapprovalProgram p, LocalDate today) => new(
        g.Id, g.BeneficiaryUserId, p.Managers.Select(m => m.UserId).ToList(), p.Name, g.Label, g.MaxAmount,
        p.HoldedAccountNumber, p.HoldedAccountId, p.ClaimBy, g.Status,
        g.Status == GrantStatus.Open && today <= p.ClaimBy);

    /// <summary>So a grant under a since-merged id is still theirs.</summary>
    internal async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info is null ? [userId] : info.AllUserIds;
    }

    internal async Task<UserInfo?> ResolveAvailablePersonAsync(Guid userId, CancellationToken ct)
    {
        var person = await users.GetUserInfoAsync(userId, ct);
        if (person is not null && !person.IsTombstone) return person;
        logger.LogWarning("Preapproval write refused for unavailable person {UserId}", userId);
        return null;
    }

    private const int NameMax = 200;
    private const int DescriptionMax = 2000;

    public async Task<PreapprovalMutationResult<Guid>> CreateProgramAsync(
        ProgramSave save, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (Validate(save, existing: null) is { } refusal) return new(Refusal: refusal);
        var account = await ResolveAccountAsync(save.HoldedAccountNumber, keep: null, ct);
        if (account is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.AccountNotFound));

        var managerIds = await ResolveManagerIdsAsync(save.ManagerUserIds, ct);
        if (managerIds is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.PersonUnavailable));

        var now = clock.GetCurrentInstant();
        var program = new PreapprovalProgram
        {
            Id = Guid.NewGuid(),
            Name = save.Name.Trim(),
            Description = PreapprovalInput.Trimmed(save.Description),
            Kind = save.Kind,
            PerPersonAmount = save.Kind == ProgramKind.FixedPerPerson ? save.PerPersonAmount : null,
            TotalCeiling = save.TotalCeiling,
            HoldedAccountNumber = save.HoldedAccountNumber,
            HoldedAccountId = account,
            AcceptsApplications = save.AcceptsApplications,
            ClaimBy = save.ClaimBy,
            Status = save.Status,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var userId in managerIds)
            program.Managers.Add(new ProgramManager
            { ProgramId = program.Id, UserId = userId, AddedByUserId = actorUserId, AddedAt = now });
        await repository.AddProgramAsync(program, ct);

        await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramCreated, program,
            $"Program \"{program.Name}\" created: {program.Kind}, " +
            $"{(program.PerPersonAmount is { } per ? $"{PreapprovalService.Euros(per)} per person, " : "")}" +
            $"ceiling {(program.TotalCeiling is { } c ? PreapprovalService.Euros(c) : "none")}, account {program.HoldedAccountNumber}, " +
            $"claim by {program.ClaimBy.ToInvariantDate()}, {program.Status}.",
            actorUserId);
        foreach (var manager in program.Managers)
            await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramManagerAdded, program,
                $"Manager added to \"{program.Name}\".", actorUserId, relatedUserId: manager.UserId);

        return new(program.Id);
    }

    public async Task<PreapprovalMutationResult> UpdateProgramAsync(
        Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var program = await repository.GetProgramAsync(programId, ct);
        if (program is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (Validate(save, program) is { } refusal) return new(refusal);

        var account = await ResolveAccountAsync(save.HoldedAccountNumber, keep: program, ct);
        if (account is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.AccountNotFound));

        var info = ToInfo(program);
        if (save.TotalCeiling is { } ceiling && ceiling < info.Granted)
            return new(new PreapprovalRefusal(PreapprovalErrorKeys.CeilingBelowCommitted, PreapprovalService.Euros(info.Granted)));

        var wanted = await ResolveManagerIdsAsync(save.ManagerUserIds, ct);
        if (wanted is null) return new(new PreapprovalRefusal(PreapprovalErrorKeys.PersonUnavailable));

        var now = clock.GetCurrentInstant();
        var changes = new List<string>();
        var oldClaimBy = program.ClaimBy;
        var wasOpen = program.Status == ProgramStatus.Open;

        Change(changes, "name", program.Name, save.Name.Trim(), v => program.Name = v);
        Change(changes, "description", program.Description, PreapprovalInput.Trimmed(save.Description), v => program.Description = v);
        var perPerson = save.Kind == ProgramKind.FixedPerPerson ? save.PerPersonAmount : null;
        Change(changes, "per-person amount", program.PerPersonAmount, perPerson, v => program.PerPersonAmount = v, Money);
        Change(changes, "ceiling", program.TotalCeiling, save.TotalCeiling, v => program.TotalCeiling = v, Money);
        Change(changes, "account", program.HoldedAccountNumber, save.HoldedAccountNumber, v => program.HoldedAccountNumber = v);
        program.HoldedAccountId = account;
        Change(changes, "accepts applications", program.AcceptsApplications, save.AcceptsApplications, v => program.AcceptsApplications = v);
        Change(changes, "claim by", program.ClaimBy, save.ClaimBy, v => program.ClaimBy = v, d => d.ToInvariantDate());
        Change(changes, "status", program.Status, save.Status, v => program.Status = v);
        Change(changes, "kind", program.Kind, save.Kind, v => program.Kind = v);
        program.UpdatedAt = now;
        var current = program.Managers.Select(m => m.UserId).ToList();
        var added = wanted.Except(current).ToList();
        var removed = current.Except(wanted).ToList();
        await repository.UpdateProgramAsync(program,
            added.Select(id => new ProgramManager { UserId = id, AddedByUserId = actorUserId, AddedAt = now }).ToList(),
            removed, ct);
        // The complete edit committed; finish audit and deadline notices independently of the browser.
        ct = CancellationToken.None;

        if (changes.Count > 0)
            await AuditProgramAsync(
                wasOpen && program.Status == ProgramStatus.Closed
                    ? PreapprovalsAuditActions.PreapprovalProgramClosed
                    : PreapprovalsAuditActions.PreapprovalProgramEdited,
                program, $"Program \"{program.Name}\" edited: {string.Join("; ", changes)}.", actorUserId);
        foreach (var id in added)
            await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramManagerAdded, program,
                $"Manager added to \"{program.Name}\".", actorUserId, relatedUserId: id);
        foreach (var id in removed)
            await AuditProgramAsync(PreapprovalsAuditActions.PreapprovalProgramManagerRemoved, program,
                $"Manager removed from \"{program.Name}\".", actorUserId, relatedUserId: id);

        if (program.ClaimBy != oldClaimBy)
            await DeadlineMovedAsync(program, actorUserId, ct);

        return PreapprovalMutationResult.Ok;
    }

    private async Task<IReadOnlyList<Guid>?> ResolveManagerIdsAsync(IReadOnlyList<Guid> requested, CancellationToken ct)
    {
        var managers = new List<Guid>();
        foreach (var id in requested.Distinct())
        {
            var person = await ResolveAvailablePersonAsync(id, ct);
            if (person is null) return null;
            managers.Add(person.Id);
        }
        return managers.Distinct().ToList();
    }

    /// <summary>
    /// Reminder markers carry the deadline they announced, so the reminders simply run again for the new date.
    /// </summary>
    private async Task DeadlineMovedAsync(PreapprovalProgram program, Guid actorUserId, CancellationToken ct)
    {
        foreach (var grant in program.Grants.Where(g => g.Status == GrantStatus.Open))
        {
            await notices.ResolveFileByAsync(grant.Id, actorUserId, ct);
            var info = ToInfo(grant, program);
            await notices.NotifyFileByAsync(info, "Preapprovals_Notice_DeadlineChanged", NotificationPriority.High, ct);
            await notices.EmailAsync(info, PreapprovalNoticeKind.DeadlineChanged, detail: null, ct);
        }
    }

    private static PreapprovalRefusal? Validate(ProgramSave save, PreapprovalProgram? existing)
    {
        if (string.IsNullOrWhiteSpace(save.Name)) return new(PreapprovalErrorKeys.NameRequired);
        if (save.Name.Trim().Length > NameMax || (save.Description?.Length ?? 0) > DescriptionMax)
            return new(PreapprovalErrorKeys.TextTooLong);
        if (save.Kind == ProgramKind.FixedPerPerson)
        {
            if (save.PerPersonAmount is not { } per) return new(PreapprovalErrorKeys.PerPersonAmountRequired);
            if (PreapprovalInput.MoneyRefusal(per) is { } amountRefusal) return amountRefusal;
        }
        else if (save.PerPersonAmount is not null)
        {
            return new(PreapprovalErrorKeys.PerPersonAmountNotAllowed);
        }
        if (save.TotalCeiling is { } ceiling && PreapprovalInput.MoneyRefusal(ceiling) is { } ceilingRefusal) return ceilingRefusal;
        // The kind decides how every grant is sized; once one exists it cannot change under them.
        if (existing is { Grants.Count: > 0 } && existing.Kind != save.Kind)
            return new(PreapprovalErrorKeys.KindLocked);
        return null;
    }

    /// <summary>An unchanged number keeps its id, so an account retired since never blocks a deadline edit.</summary>
    private async Task<string?> ResolveAccountAsync(int accountNumber, PreapprovalProgram? keep, CancellationToken ct)
    {
        if (keep is not null && keep.HoldedAccountNumber == accountNumber) return keep.HoldedAccountId;
        var option = (await finance.ListExpenseAccountsAsync(activeOnly: true, ct))
            .FirstOrDefault(a => a.AccountNum == accountNumber);
        return option?.AccountId;
    }

    private static void Change<T>(
        List<string> changes, string field, T before, T after, Action<T> apply, Func<T, string>? show = null)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        show ??= v => v?.ToString() ?? "none";
        changes.Add($"{field} {show(before)} → {show(after)}");
        apply(after);
    }

    private static string Money(decimal? value) => value is { } v ? PreapprovalService.Euros(v) : "none";

    private const int LabelMax = 200;
    private const int NoteMax = 2000;

    // Scoped requests must share the pending-application check and save boundary.
    private static readonly TrackedLock ApplicationLock = new("Preapprovals.Apply");

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

        var beneficiary = await ResolveAvailablePersonAsync(beneficiaryUserId, ct);
        if (beneficiary is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.PersonUnavailable));
        var beneficiaryId = beneficiary.Id;
        var now = clock.GetCurrentInstant();
        var grant = NewGrant(program, beneficiaryId, sized.Value, PreapprovalInput.Trimmed(label), GrantStatus.Open, now);
        await PersistIssuedGrantsAsync(program, [grant], actorUserId,
            issued => $"Issued {Euros(issued.MaxAmount)} under \"{program.Name}\"{LabelSuffix(issued)} to {beneficiaryId}; claim by {program.ClaimBy.ToInvariantDate()}.", ct);
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

        var recipients = new List<Guid>();
        foreach (var id in beneficiaryUserIds.Distinct())
        {
            var beneficiary = await ResolveAvailablePersonAsync(id, ct);
            if (beneficiary is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.PersonUnavailable));
            recipients.Add(beneficiary.Id);
        }
        var ids = recipients.Distinct().ToList();
        if (ids.Count == 0) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.NoBeneficiaries));

        var each = program.PerPersonAmount!.Value;
        if (PreapprovalInput.MoneyRefusal(each) is { } amountRefusal) return new(Refusal: amountRefusal);
        if (CeilingCheck(program, each * ids.Count) is { } over) return new(Refusal: over);

        var now = clock.GetCurrentInstant();
        var grants = ids.Select(id => NewGrant(program, id, each, null, GrantStatus.Open, now)).ToList();
        await PersistIssuedGrantsAsync(program, grants, actorUserId,
            grant => $"Issued {Euros(grant.MaxAmount)} under \"{program.Name}\" to {grant.BeneficiaryUserId} (batch of {grants.Count}); claim by {program.ClaimBy.ToInvariantDate()}.", ct);
        return new(grants.Select(g => g.Id).ToList());
    }

    private async Task PersistIssuedGrantsAsync(
        PreapprovalProgram program, IReadOnlyList<PreapprovalGrant> grants, Guid actorUserId,
        Func<PreapprovalGrant, string> describe, CancellationToken ct)
    {
        foreach (var grant in grants)
        {
            grant.DecidedByUserId = actorUserId;
            grant.DecidedAt = grant.CreatedAt;
        }
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
        using var gate = await ApplicationLock.AcquireAsync(logger, ct);
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
        if (PreapprovalInput.MoneyRefusal(amount) is { } amountRefusal) return new(Refusal: amountRefusal);

        var applicant = await ResolveAvailablePersonAsync(applicantUserId, ct);
        if (applicant is null) return new(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.PersonUnavailable));
        var ids = applicant.AllUserIds;
        var now = clock.GetCurrentInstant();
        var pending = program.Grants.FirstOrDefault(g => g.Status == GrantStatus.Applied && ids.Contains(g.BeneficiaryUserId));
        if (pending is not null)
        {
            // A second submit edits the pending application; the managers' prompt is re-sent so
            // the newest text is the one they see.
            pending.ApplicationNote = PreapprovalInput.Trimmed(note);
            pending.RequestedAmount = requestedAmount;
            pending.MaxAmount = amount;
            pending.UpdatedAt = now;
            await repository.UpdateGrantAsync(pending, ct);
            // The grant committed; finish its audit and notices after the browser disconnects.
            ct = CancellationToken.None;
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantApplied, pending,
                $"Application to \"{program.Name}\" edited: {Euros(amount)}.", applicantUserId);
            await notices.ResolveRequestAsync(pending.Id, applicantUserId, ct);
            await AnnounceApplicationAsync(info, ToInfo(pending, program), ct);
            return new(pending.Id);
        }

        var grant = NewGrant(program, ids[0], amount, null, GrantStatus.Applied, now);
        grant.ApplicationNote = PreapprovalInput.Trimmed(note);
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
        Guid programId, Guid grantId, decimal? amount, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null || grant.ProgramId != programId) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
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
        await notices.ResolveRequestAsync(grant.Id, actorUserId, ct);
        await AnnounceIssuedAsync(ToInfo(grant, program), ct);
        return PreapprovalMutationResult.Ok;
    }

    public async Task<PreapprovalMutationResult> DeclineAsync(
        Guid programId, Guid grantId, string? note, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null || grant.ProgramId != programId) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
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

        var reason = PreapprovalInput.Trimmed(note);
        await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantDeclined, grant,
            $"Application to \"{grant.Program.Name}\" declined{(reason is null ? "" : $": {reason}")}.", actorUserId);
        await notices.ResolveRequestAsync(grant.Id, actorUserId, ct);
        var info = ToInfo(grant);
        await notices.NotifyDecidedAsync(info, "Preapprovals_Notice_Declined", reason, ct);
        await notices.EmailAsync(info, PreapprovalNoticeKind.Declined, reason, ct);
        return PreapprovalMutationResult.Ok;
    }

    public async Task<PreapprovalMutationResult> RevokeAsync(
        Guid programId, Guid grantId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null || grant.ProgramId != programId) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (grant.Status is not (GrantStatus.Open or GrantStatus.Claimed))
            return new(new PreapprovalRefusal(PreapprovalErrorKeys.WrongStatus));
        if ((reason?.Length ?? 0) > NoteMax) return new(new PreapprovalRefusal(PreapprovalErrorKeys.TextTooLong));

        var holdingReportId = grant.Status == GrantStatus.Claimed ? grant.ExpenseReportId : null;
        // The amount and any report id stay: the row is the record of what was committed.
        grant.Status = GrantStatus.Revoked;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);
        // The grant committed; finish its audit and notices after the browser disconnects.
        ct = CancellationToken.None;

        var why = PreapprovalInput.Trimmed(reason);
        await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantRevoked, grant,
            $"Revoked {Euros(grant.MaxAmount)} under \"{grant.Program.Name}\"{LabelSuffix(grant)}" +
            $"{(holdingReportId is { } r ? $" while held by expense report {r}" : "")}{(why is null ? "" : $": {why}")}.",
            actorUserId);
        await notices.ResolveFileByAsync(grant.Id, actorUserId, ct);
        var info = ToInfo(grant);
        await notices.NotifyDecidedAsync(info, "Preapprovals_Notice_Revoked", why, ct, holdingReportId: holdingReportId);
        await notices.EmailAsync(info, PreapprovalNoticeKind.Revoked, why, ct);
        return PreapprovalMutationResult.Ok;
    }

    public async Task<PreapprovalMutationResult> ChangeAmountAsync(
        Guid programId, Guid grantId, decimal amount, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null || grant.ProgramId != programId) return new(new PreapprovalRefusal(PreapprovalErrorKeys.NotFound));
        if (grant.Status is not (GrantStatus.Open or GrantStatus.Claimed))
            return new(new PreapprovalRefusal(PreapprovalErrorKeys.WrongStatus));
        if (PreapprovalInput.MoneyRefusal(amount) is { } amountRefusal) return new(amountRefusal);
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
            $"{(grant.Status == GrantStatus.Claimed && grant.ExpenseReportId is { } r ? $" (held by expense report {r}; its cap follows at next submit)" : "")}.",
            actorUserId);
        var info = ToInfo(grant, program);
        await notices.NotifyDecidedAsync(info, "Preapprovals_Notice_AmountChanged", null, ct, previousAmount: old);
        await notices.EmailAsync(info, PreapprovalNoticeKind.AmountChanged, null, ct, previousAmount: old);
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
        return PreapprovalInput.MoneyRefusal(value) is { } refusal ? (0, refusal) : (value, null);
    }

    /// <summary>Granted + delta ≤ ceiling, else a refusal naming the headroom.</summary>
    private static PreapprovalRefusal? CeilingCheck(PreapprovalProgram program, decimal delta)
    {
        if (program.TotalCeiling is not { } ceiling) return null;
        var granted = program.Grants
            .Where(g => PreapprovalGrant.CountsAgainstCeiling(g.Status))
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
        await notices.NotifyFileByAsync(info, "Preapprovals_Notice_Issued", NotificationPriority.Normal, ct);
        await notices.EmailAsync(info, PreapprovalNoticeKind.Issued, detail: null, ct);
    }

    private async Task AnnounceApplicationAsync(ProgramInfo program, GrantInfo info, CancellationToken ct)
    {
        await notices.SafeAsync(() => notices.NotifyManagersAsync(program, info, ct), "notify managers", info.Id);
        await notices.SafeAsync(() => notices.EmailManagersAsync(program, info, ct), "email managers", info.Id);
    }

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
        var rows = await repository.GetRowsForUsersAsync(await AllIdsAsync(userId, ct), ct);
        var grants = rows.Grants.Select(g => new
        {
            Program = g.Program.Name,
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
            ManagerOf = rows.ManagerRows.Select(m => new { Program = m.Program.Name, AddedAt = m.AddedAt.ToIso8601() }).ToList(),
            ManagersAdded = rows.ManagersAdded.Select(m => new { Program = m.Program.Name, AddedAt = m.AddedAt.ToIso8601() }).ToList(),
            GrantsDecided = rows.GrantsDecided.Select(g => new
            {
                Program = g.Program.Name,
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
            await notices.ResolveRequestAsync(grantId, actorUserId, ct);
            await auditLog.LogAsync(PreapprovalsAuditActions.PreapprovalMergeDroppedApplication, PreapprovalsAuditSubjectTypes.Grant, grantId,
                $"Duplicate pending application dropped on merge of {mergedFromUserId} into {mergedToUserId}.",
                actorUserId, targetId: mergedToUserId, targetType: AuditSubjectTypes.User);
        }
    }
}
