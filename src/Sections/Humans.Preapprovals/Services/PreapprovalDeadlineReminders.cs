using System.Transactions;
using Humans.Base.Interfaces;
using Humans.Preapprovals.Data;
using static Humans.Preapprovals.Services.PreapprovalService;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>Never changes a grant or a program: a passed deadline is left exactly as the people involved left it.</summary>
internal sealed class PreapprovalDeadlineReminders(
    IPreapprovalRepository repository,
    PreapprovalNotices notices,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<PreapprovalService> logger) : IApplicationService
{
    internal const string ReminderJobName = "PreapprovalRemindersJob";
    private const int FirstReminderDays = 14;
    private const int LastReminderDays = 3;

    public async Task SendDeadlineRemindersAsync(CancellationToken ct = default)
    {
        var today = clock.GetCurrentInstant().InZone(MadridZone).Date;
        var open = await repository.GetGrantsByStatusAsync(GrantStatus.Open, ct);
        var due = open
            .Select(g => (Grant: g, Kind: ReminderKindFor(today, g.Program.ClaimBy)))
            .Where(x => x.Kind is not null)
            .ToList();
        if (due.Count == 0) return;

        var sent = (await repository.GetRemindersSentAsync(due.Select(x => x.Grant.Id).ToList(), ct))
            .Select(r => (r.GrantId, r.Kind, r.ClaimBy))
            .ToHashSet();

        var failures = new List<Exception>();
        foreach (var (grant, kind) in due)
        {
            if (sent.Contains((grant.Id, kind!.Value, grant.Program.ClaimBy))) continue;
            try
            {
                await RemindAsync(grant, kind.Value, today, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex, "Pre-approval {Kind} reminder failed for grant {GrantId}", kind, grant.Id);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("Failed to complete one or more pre-approval deadline reminders.", failures);
    }

    private async Task RemindAsync(PreapprovalGrant grant, ReminderKind kind, LocalDate today, CancellationToken ct)
    {
        PreapprovalGrant? fresh;
        GrantInfo info;
        using (var scope = new TransactionScope(TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            // Re-read inside the scope: a claim or revoke since the list was built means no reminder.
            fresh = await repository.GetGrantAsync(grant.Id, ct);
            if (fresh is null || fresh.Status != GrantStatus.Open || fresh.Program.ClaimBy != grant.Program.ClaimBy)
                return;

            info = ToInfo(fresh);
            var priority = kind == ReminderKind.D3 ? NotificationPriority.High : NotificationPriority.Normal;
            await notices.NotifyFileByAsync(info, "Preapprovals_Notice_Reminder", priority, ct, propagateFailures: true);
            await notices.EmailAsync(info, PreapprovalNoticeKind.DeadlineReminder, detail: null, ct, propagateFailures: true);
            await repository.AddReminderSentAsync(new ReminderSent
            {
                GrantId = fresh.Id,
                Kind = kind,
                ClaimBy = fresh.Program.ClaimBy,
                SentAt = clock.GetCurrentInstant()
            }, ct);
            scope.Complete();
        }
        await auditLog.LogAsync(PreapprovalsAuditActions.PreapprovalReminderSent, AuditEntityTypes.Grant, fresh.Id,
            $"{(kind == ReminderKind.D3 ? LastReminderDays : FirstReminderDays)}-day reminder sent: " +
            $"{Euros(fresh.MaxAmount)} under \"{fresh.Program.Name}\", claim by {fresh.Program.ClaimBy.ToInvariantDate()} ({info.DaysLeft(today)} days left).",
            ReminderJobName, relatedEntityId: fresh.ProgramId, relatedEntityType: AuditEntityTypes.Program);
    }

    /// <summary>D3 inside the last three days, D14 inside the last fourteen, nothing past the deadline.</summary>
    internal static ReminderKind? ReminderKindFor(LocalDate today, LocalDate claimBy)
    {
        var daysLeft = Period.Between(today, claimBy, PeriodUnits.Days).Days;
        if (daysLeft < 0) return null;
        if (daysLeft <= LastReminderDays) return ReminderKind.D3;
        if (daysLeft <= FirstReminderDays) return ReminderKind.D14;
        return null;
    }
}
