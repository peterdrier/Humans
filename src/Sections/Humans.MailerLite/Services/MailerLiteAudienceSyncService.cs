using Humans.AuditLog.Contracts;
using Humans.MailerLite.Data;
using Humans.MailerLite.Domain;
using Humans.MailerLite.Services.Dtos;
using Humans.MailerLite.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.MailerLite.Services;

/// <summary>
/// Orchestrates audience computation, ML state diffing, and the apply step.
/// Lives in the Application layer; the section's sync state goes through
/// <see cref="IMailerLiteRepository"/>.
/// </summary>
internal sealed class MailerLiteAudienceSyncService(
    IMailerLiteService ml,
    IUserEmailService emails,
    IAuditLogService audit,
    IMailerLiteRepository repository,
    IClock clock,
    IEnumerable<IMailerLiteAudience> audiences,
    ILogger<MailerLiteAudienceSyncService> logger) : IMailerLiteAudienceSyncService, IMailerLiteAudienceSync
{
    private const string HumansGroupPrefix = "Humans - ";
    private const string JobName = nameof(MailerLiteAudienceSyncService);

    private static readonly HashSet<string> UnsubscribedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "unsubscribed", "bounced", "junk" };

    public async Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(
        Guid? actorUserId = null, CancellationToken ct = default)
    {
        var results = new List<AudienceSyncResult>();
        foreach (var audience in audiences)
        {
            try
            {
                results.Add(await SyncAsync(audience, actorUserId, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audience sync failed for {Audience}", audience.Key);
            }
        }
        return results;
    }

    /// <summary>
    /// The <see cref="IMailerLiteAudienceSync"/> half, driven by <c>MailerLiteAudienceSyncJob</c>.
    /// The scheduled run has no actor, and the job logs only how many audiences completed —
    /// so the contracts leaf carries an <c>int</c> rather than the section's
    /// <see cref="AudienceSyncResult"/> list.
    /// </summary>
    public async Task<int> SyncAllAudiencesAsync(CancellationToken cancellationToken = default)
    {
        var results = await SyncAllAsync(actorUserId: null, cancellationToken);
        return results.Count;
    }

    public async Task<AudienceStats> ComputeStatsAsync(IMailerLiteAudience audience, CancellationToken ct = default)
    {
        var snapshot = await BuildSnapshotAsync(ct);
        return await BuildStatsForAudienceAsync(audience, snapshot, ct);
    }

    public async Task<IReadOnlyList<AudienceStats>> ComputeAllStatsAsync(CancellationToken ct = default)
    {
        var snapshot = await BuildSnapshotAsync(ct);

        // Single read of the section's own sync-state table; map by key in memory. Grouped
        // rather than ToDictionary'd: the upsert lock keeps it one row per key, and a stale
        // number beats a dashboard that 500s if a duplicate ever slips past anyway.
        var lastByKey = (await repository.GetSyncStatesAsync(ct))
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.LastSyncAt)!, StringComparer.Ordinal);

        var rows = new List<AudienceStats>();
        foreach (var audience in audiences)
        {
            var stats = await BuildStatsForAudienceAsync(audience, snapshot, ct);
            if (lastByKey.TryGetValue(audience.Key, out var state))
                stats = stats with
                {
                    LastSyncAt = state.LastSyncAt,
                    LastSyncSummary = state.Summary,
                };
            rows.Add(stats);
        }
        return rows;
    }

    public async Task<AudienceSyncResult> SyncAsync(
        IMailerLiteAudience audience, Guid? actorUserId = null, CancellationToken ct = default)
    {
        if (!audience.MailerLiteGroupName.StartsWith(HumansGroupPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Audience '{audience.Key}' targets group '{audience.MailerLiteGroupName}' " +
                $"which does not start with '{HumansGroupPrefix}'.");

        var memberUserIds = await audience.ComputeMemberUserIdsAsync(ct);
        var userEmailMap = await emails.GetNotificationTargetEmailsAsync(memberUserIds.ToList(), ct);
        var droppedNoEmail = memberUserIds.Count - userEmailMap.Count;
        if (droppedNoEmail > 0)
            logger.LogInformation(
                "Audience {Audience}: dropped {Count} candidates with no notification-target email",
                audience.Key, droppedNoEmail);

        var groups = await ml.ListGroupsAsync(ct);
        var group = groups.FirstOrDefault(g => string.Equals(g.Name, audience.MailerLiteGroupName, StringComparison.Ordinal))
            ?? await ml.CreateGroupAsync(audience.MailerLiteGroupName, ct);

        // Single snapshot reused for diff + apply; per-write methods don't invalidate the subscriber cache.
        var subscribers = new List<MailerLiteSubscriber>();
        await foreach (var s in ml.ListSubscribersAsync(ct)) subscribers.Add(s);
        var byEmail = subscribers.ToDictionary(s => NormalizeEmail(s.Email), s => s, StringComparer.Ordinal);
        var currentGroupMemberIds = subscribers
            .Where(s => s.GroupIds.Contains(group.Id, StringComparer.Ordinal))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        var toBulkImport = new List<string>();
        var toAssign = new List<string>();
        var keepSubscriberIds = new HashSet<string>(StringComparer.Ordinal);
        int excluded = 0, alreadyAssigned = 0;

        foreach (var (_, email) in userEmailMap)
        {
            var norm = NormalizeEmail(email);
            if (!byEmail.TryGetValue(norm, out var sub))
            {
                toBulkImport.Add(email);
                continue;
            }
            if (UnsubscribedStatuses.Contains(sub.Status))
            {
                excluded++;
                continue;
            }
            keepSubscriberIds.Add(sub.Id);
            if (currentGroupMemberIds.Contains(sub.Id))
                alreadyAssigned++;
            else
                toAssign.Add(sub.Id);
        }

        var toUnassign = currentGroupMemberIds.Except(keepSubscriberIds, StringComparer.Ordinal).ToList();

        int created = 0, assigned = 0, unassigned = 0, errors = 0;

        if (toBulkImport.Count > 0)
        {
            try
            {
                var bulk = await ml.BulkImportSubscribersToGroupAsync(group.Id, toBulkImport, ct);
                created = bulk.Created;
                errors += bulk.Errors;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BulkImport failed for {Audience}", audience.Key);
                errors += toBulkImport.Count;
            }
        }

        foreach (var subId in toAssign)
        {
            try
            {
                await ml.AssignSubscriberToGroupAsync(subId, group.Id, ct);
                assigned++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Assign failed for {Sub} in {Audience}", subId, audience.Key);
                errors++;
            }
        }

        foreach (var subId in toUnassign)
        {
            try
            {
                await ml.UnassignSubscriberFromGroupAsync(subId, group.Id, ct);
                unassigned++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unassign failed for {Sub} in {Audience}", subId, audience.Key);
                errors++;
            }
        }

        var result = new AudienceSyncResult(
            audience.Key, group.Id, group.Name,
            Candidates: userEmailMap.Count,
            ExcludedUnsubscribed: excluded,
            Created: created,
            Assigned: assigned,
            AlreadyAssigned: alreadyAssigned,
            Unassigned: unassigned,
            Errors: errors);

        // The section's own state first, so the audit entry can point at a row that exists.
        var state = await repository.UpsertSyncStateAsync(new MailerLiteSyncState
        {
            Key = result.Key,
            LastSyncAt = clock.GetCurrentInstant(),
            Summary = result.FormatSummary(),
            GroupId = result.GroupId,
            GroupName = result.GroupName,
            Candidates = result.Candidates,
            ExcludedUnsubscribed = result.ExcludedUnsubscribed,
            Created = result.Created,
            Assigned = result.Assigned,
            AlreadyAssigned = result.AlreadyAssigned,
            Unassigned = result.Unassigned,
            Errors = result.Errors,
        }, ct);

        var description = $"Audience '{audience.DisplayName}' synced to " +
                          $"'{result.GroupName}': {result.FormatSummary()}";

        if (actorUserId is Guid actor)
        {
            await audit.LogAsync(
                AuditAction.MailerLiteAudienceSyncCompleted,
                entityType: "MailerLiteAudience", entityId: state.Id,
                description: description,
                actorUserId: actor);
        }
        else
        {
            await audit.LogAsync(
                AuditAction.MailerLiteAudienceSyncCompleted,
                entityType: "MailerLiteAudience", entityId: state.Id,
                description: description,
                jobName: JobName);
        }

        return result;
    }

    private async Task<MlSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var subscribers = new List<MailerLiteSubscriber>();
        await foreach (var s in ml.ListSubscribersAsync(ct)) subscribers.Add(s);
        var byEmail = subscribers.ToDictionary(s => NormalizeEmail(s.Email), s => s, StringComparer.Ordinal);
        var groups = await ml.ListGroupsAsync(ct);
        return new MlSnapshot(byEmail, groups);
    }

    private async Task<AudienceStats> BuildStatsForAudienceAsync(
        IMailerLiteAudience audience, MlSnapshot snapshot, CancellationToken ct)
    {
        var memberUserIds = await audience.ComputeMemberUserIdsAsync(ct);
        var userEmailMap = await emails.GetNotificationTargetEmailsAsync(memberUserIds.ToList(), ct);

        var group = snapshot.Groups.FirstOrDefault(g =>
            string.Equals(g.Name, audience.MailerLiteGroupName, StringComparison.Ordinal));

        int excluded = 0, inGroup = 0;
        foreach (var (_, email) in userEmailMap)
        {
            if (!snapshot.ByEmail.TryGetValue(NormalizeEmail(email), out var sub)) continue;
            if (UnsubscribedStatuses.Contains(sub.Status)) excluded++;
            else if (group is not null && sub.GroupIds.Contains(group.Id, StringComparer.Ordinal)) inGroup++;
        }

        return new AudienceStats(
            audience.Key,
            audience.DisplayName,
            audience.MailerLiteGroupName,
            Candidates: userEmailMap.Count,
            ExcludedUnsubscribed: excluded,
            CurrentlyInGroup: inGroup,
            LastSyncAt: null,
            LastSyncSummary: null);
    }

    private static string NormalizeEmail(string email) =>
        email.Trim().ToLowerInvariant();

    private sealed record MlSnapshot(
        IReadOnlyDictionary<string, MailerLiteSubscriber> ByEmail,
        IReadOnlyList<MailerLiteGroup> Groups);
}
