using static Humans.Workgroups.Services.WorkgroupJournal;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>Guards and crosscut calls, kept apart so the rule files read as rules.</summary>
internal sealed partial class WorkgroupService
{
    private static WorkgroupInfo ToInfo(Workgroup w) => WorkgroupRegisterReader.ToInfo(w);

    private static WorkgroupRefusal? RequireReasons(string? reasons)
    {
        if (string.IsNullOrWhiteSpace(reasons))
            return new(WorkgroupErrorKeys.ReasonsRequired);
        if (reasons.Trim().Length > 4000)
            return new(WorkgroupErrorKeys.TextTooLong, 4000);
        return null;
    }

    private async Task<WorkgroupMutationResult<WorkgroupApplication>> ValidateApplicationAsync(
        WorkgroupApplication application, CancellationToken ct)
    {
        if (ValidateRegisterFields(application.Name, application.Purpose, application.Deliverable, application.DiscordChannelUrl) is { } refusal)
            return new(Refusal: refusal);
        if (application.SecondCoordinatorUserId is not { } second)
            return new(application);
        var coordinator = await access.ResolveMemberTargetAsync(second, ct);
        return coordinator is null
            ? new(Refusal: new(WorkgroupErrorKeys.NotFound))
            : new(application with { SecondCoordinatorUserId = coordinator.Value });
    }

    private static WorkgroupRefusal? ValidateRegisterFields(string name, string purpose, string deliverable, string? discordChannelUrl)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new(WorkgroupErrorKeys.NameRequired);
        if (string.IsNullOrWhiteSpace(purpose))
            return new(WorkgroupErrorKeys.PurposeRequired);
        if (string.IsNullOrWhiteSpace(deliverable))
            return new(WorkgroupErrorKeys.DeliverableRequired);
        if (!string.IsNullOrWhiteSpace(discordChannelUrl) && WorkgroupRegisterReader.SafeHttpLink(discordChannelUrl) is null)
            return new(WorkgroupErrorKeys.DiscordUrlInvalid);
        return null;
    }

    private static void ApplyFields(
        Workgroup w,
        string name,
        string purpose,
        string deliverable,
        WorkgroupDeliverableKind kind,
        WorkgroupAudience audience,
        LocalDate? targetDate,
        string? discordChannelUrl,
        bool isSensitive)
    {
        w.Name = name.Trim();
        w.Purpose = purpose.Trim();
        w.Deliverable = deliverable.Trim();
        w.DeliverableKind = kind;
        w.Audience = audience;
        w.TargetDate = targetDate;
        w.DiscordChannelUrl = Trimmed(discordChannelUrl);
        w.IsSensitive = isSensitive ? true : null;
    }

    /// <summary>
    /// Suffixed until free. Reserved route segments count as taken, so a group called "Apply" gets <c>apply-2</c>
    /// rather than shadowing the form.
    /// </summary>
    private async Task<WorkgroupMutationResult<string>> ReserveSlugAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var baseSlug = SlugHelper.GenerateSlug(name);
        if (string.IsNullOrEmpty(baseSlug))
            baseSlug = "workgroup";

        // Name allows 200 characters but Slug holds 100, with room for a "-100" suffix: trim rather than fail on insert.
        if (baseSlug.Length > SlugBudget)
            baseSlug = baseSlug[..SlugBudget].TrimEnd('-');

        var candidate = baseSlug;
        for (var suffix = 2; ; suffix++)
        {
            if (!ReservedSlugs.Contains(candidate, StringComparer.Ordinal)
                && !await repository.SlugTakenAsync(candidate, exceptId, ct))
            {
                return new(candidate);
            }

            // A hundred groups sharing one name is a data problem, not a naming problem.
            if (suffix > 100)
                return new(Refusal: new(WorkgroupErrorKeys.NameTaken));

            candidate = $"{baseSlug}-{suffix}";
        }
    }

    private const int SlugBudget = 96;

    /// <summary>A group that stops being Active has nobody left to decide its join requests. Nothing is logged.</summary>
    private static IReadOnlyList<WorkgroupMember> ClosePendingRequests(Workgroup w, Instant now)
    {
        var pending = w.Members.Where(m => m.LeftAt is null && m.IsPending == true).ToList();
        foreach (var m in pending) m.LeftAt = now;
        return pending;
    }

    /// <summary>The one place a group ends, whether a member marked it done or the Secretary closed it.</summary>
    private async Task EndAsync(
        Workgroup w,
        Guid actorUserId,
        WorkgroupDormantReason reason,
        string? reasons,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        w.Status = WorkgroupStatus.Dormant;
        w.DormantReason = reason;
        w.Reasons = Trimmed(reasons) ?? w.Reasons;
        w.EndedAt = now;
        w.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(w, ct,
            [SystemEntry(w.Id, WorkgroupLogKind.Ended, now, Trimmed(reasons) ?? reason.ToString(),
                authorUserId: reason == WorkgroupDormantReason.Quiet ? null : actorUserId)],
            ClosePendingRequests(w, now));
        ct = CancellationToken.None;

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupClosed, w, $"Group ended ({reason})", actorUserId);

        var info = ToInfo(w);
        var coordinators = info.CoordinatorUserIds();
        await notices.NotifyAsync(coordinators, WorkgroupsNotificationSources.WorkgroupRegistrationDecided,
            "Enum_WorkgroupLogKind_Ended", info, Trimmed(reasons), ct);
        await notices.EmailAsync(coordinators, WorkgroupNoticeKind.Ended, info, Trimmed(reasons), ct);

        // Dormant: the access source now returns Viewer.
        await DriveFolders.RequestDriveSyncAsync(w, ct);
        await Budgets.SetAccountActiveAsync(w, isActive: false, ct);
    }
}
