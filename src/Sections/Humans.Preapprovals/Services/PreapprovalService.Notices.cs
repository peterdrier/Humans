using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Preapprovals.Services;

/// <summary>Email and notification failures never fail the write: the row and the audit entry are the record.</summary>
internal sealed partial class PreapprovalService
{
    private static readonly System.Resources.ResourceManager NoticeResources = new(typeof(PreapprovalsResource));

    internal static string FileBySourceKey(Guid grantId) => $"pre-grant:{grantId}";
    internal static string RequestSourceKey(Guid grantId) => $"pre-request:{grantId}";

    private static string MemberUrl(Guid programId) => $"/Preapprovals/{programId}";
    private static string ManageUrl(Guid programId) => $"/Preapprovals/Admin/{programId}";

    private Task AuditGrantAsync(
        AuditActionDefinition action, Domain.PreapprovalGrant grant, string description, Guid actorUserId,
        Guid? expenseReportId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: expenseReportId ?? grant.ProgramId,
            relatedEntityType: expenseReportId is null ? AuditEntityTypes.Program : AuditEntityTypes.ExpenseReport);

    private Task AuditProgramAsync(
        AuditActionDefinition action, Domain.PreapprovalProgram program, string description, Guid actorUserId,
        Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditEntityTypes.User);

    /// <summary>Clears the beneficiary's standing "file by" prompt (issued, released, reminders, deadline moved).</summary>
    private Task ResolveFileByAsync(Guid grantId, Guid? actorUserId, CancellationToken ct) =>
        SafeAsync(() => notificationResolve.ResolveBySourceKeyAsync(
            PreapprovalsNotificationSources.PreapprovalFileBy, FileBySourceKey(grantId), actorUserId, ct), "resolve file-by", grantId);

    /// <summary>Clears the managers' "application received" prompt (approved, declined, dropped).</summary>
    private Task ResolveRequestAsync(Guid grantId, Guid? actorUserId, CancellationToken ct) =>
        SafeAsync(() => notificationResolve.ResolveBySourceKeyAsync(
            PreapprovalsNotificationSources.PreapprovalApplicationReceived, RequestSourceKey(grantId), actorUserId, ct),
            "resolve application-received", grantId);

    /// <summary>Keyed by the grant, so one resolve clears every variant (issued, released, reminder, deadline moved).</summary>
    private Task NotifyFileByAsync(GrantInfo info, string titleKey, NotificationPriority priority, CancellationToken ct) =>
        NotifyAsync([info.BeneficiaryUserId], PreapprovalsNotificationSources.PreapprovalFileBy, NotificationClass.Actionable,
            priority, titleKey, info, "Preapprovals_Notice_FileBy_Body", MemberUrl(info.ProgramId),
            FileBySourceKey(info.Id), ct);

    /// <summary>Informational unless revocation affects a holding report; no source key.</summary>
    private Task NotifyDecidedAsync(GrantInfo info, string titleKey, string? detail, CancellationToken ct,
        decimal? previousAmount = null, Guid? holdingReportId = null) =>
        NotifyAsync([info.BeneficiaryUserId], PreapprovalsNotificationSources.PreapprovalGrantDecided,
            holdingReportId is not null
                ? NotificationClass.Actionable
                : NotificationClass.Informational,
            NotificationPriority.Normal, titleKey, info, "Preapprovals_Notice_Decided_Body",
            holdingReportId is { } reportId
                ? $"/Expenses/{reportId}"
                : MemberUrl(info.ProgramId),
            sourceKey: null, ct, detail, previousAmount);

    /// <summary>To every manager of the program: someone applied. Resolved on decision.</summary>
    private async Task NotifyManagersAsync(ProgramInfo program, GrantInfo info, CancellationToken ct)
    {
        var applicant = await users.GetUserInfoAsync(info.BeneficiaryUserId, ct);
        await NotifyAsync(program.ManagerUserIds, PreapprovalsNotificationSources.PreapprovalApplicationReceived,
            NotificationClass.Actionable, NotificationPriority.Normal, "Preapprovals_Notice_ApplicationReceived",
            info, "Preapprovals_Notice_ApplicationReceived_Body", ManageUrl(info.ProgramId),
            RequestSourceKey(info.Id), ct, detail: applicant?.BurnerName ?? "A member");
    }

    private async Task NotifyAsync(
        IReadOnlyList<Guid> recipientUserIds, NotificationSourceDefinition source, NotificationClass notificationClass,
        NotificationPriority priority, string titleKey, GrantInfo info, string bodyKey, string actionUrl,
        string? sourceKey, CancellationToken ct, string? detail = null, decimal? previousAmount = null)
    {
        if (recipientUserIds.Count == 0) return;
        try
        {
            var people = await users.GetUserInfosAsync(recipientUserIds, ct);
            // In-app notices are keyed by user id: deliver to the live id the read resolved to.
            var recipients = recipientUserIds.Select(id =>
            {
                var person = people.GetValueOrDefault(id);
                var language = person?.PreferredLanguage;
                return (Id: person?.Id ?? id, Language: language.IsSupportedCultureCode() ? language! : CultureCatalog.DefaultCultureCode);
            }).DistinctBy(person => person.Id);
            foreach (var group in recipients.GroupBy(person => person.Language, StringComparer.OrdinalIgnoreCase))
            {
                var culture = CultureInfo.GetCultureInfo(group.Key);
                using var cultureScope = new CultureScope(culture.Name, logger);
                var title = $"{NoticeResources.GetString(titleKey, culture)}: {info.ProgramName}";
                var body = string.Format(culture, NoticeResources.GetString(bodyKey, culture) ?? bodyKey,
                    info.ProgramName, info.Label ?? "", info.MaxAmount.ToEuro(), info.ClaimBy.ToDate(),
                    previousAmount?.ToEuro() ?? detail ?? "");
                await notifications.SendAsync(source, notificationClass, priority, title,
                    group.Select(person => person.Id).ToList(), body.Trim(),
                    actionUrl: actionUrl, sourceKey: sourceKey, cancellationToken: ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send the pre-approval {Title} notice for grant {GrantId}", titleKey, info.Id);
        }
    }

    private Task EmailAsync(GrantInfo info, PreapprovalNoticeKind kind, string? detail, CancellationToken ct,
        decimal? previousAmount = null) =>
        EmailAsync([info.BeneficiaryUserId], info, kind, detail, MemberUrl(info.ProgramId), ct, previousAmount);

    /// <summary>One email per manager: who applied and for what.</summary>
    private async Task EmailManagersAsync(ProgramInfo program, GrantInfo info, CancellationToken ct)
    {
        var applicant = await users.GetUserInfoAsync(info.BeneficiaryUserId, ct);
        var detail = applicant?.BurnerName ?? "A member";
        if (!string.IsNullOrEmpty(info.ApplicationNote)) detail += ": " + info.ApplicationNote;
        await EmailAsync(program.ManagerUserIds, info, PreapprovalNoticeKind.ApplicationReceived, detail,
            ManageUrl(info.ProgramId), ct);
    }

    private async Task EmailAsync(
        IReadOnlyList<Guid> recipientUserIds, GrantInfo info, PreapprovalNoticeKind kind, string? detail,
        string url, CancellationToken ct, decimal? previousAmount = null)
    {
        var ids = recipientUserIds.Distinct().ToList();
        if (ids.Count == 0) return;
        try
        {
            var addresses = await userEmails.GetNotificationTargetEmailsAsync(ids, ct);
            var infos = await users.GetUserInfosAsync(ids, ct);
            foreach (var id in ids)
            {
                if (!addresses.TryGetValue(id, out var address) || string.IsNullOrWhiteSpace(address))
                {
                    logger.LogWarning("Skipping pre-approval {Kind} email for {UserId} on grant {GrantId}: no effective email",
                        kind, id, info.Id);
                    continue;
                }
                infos.TryGetValue(id, out var person);
                var language = person?.PreferredLanguage;
                await email.SendAsync(emailFactory.Notice(new PreapprovalNoticeRequest(
                    id, address, person?.BurnerName, kind, info.ProgramName, info.Label, info.MaxAmount, info.ClaimBy,
                    url, detail, language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode, previousAmount)), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue the pre-approval {Kind} email for grant {GrantId}", kind, info.Id);
        }
    }

    private async Task SafeAsync(Func<Task> action, string what, Guid grantId)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to {What} for pre-approval grant {GrantId}", what, grantId);
        }
    }
}
