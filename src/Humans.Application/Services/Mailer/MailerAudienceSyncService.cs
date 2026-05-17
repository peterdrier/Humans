using System.Text.Json;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Mailer;

/// <summary>
/// Orchestrates audience computation, ML state diffing, and the apply step.
/// Lives in the Application layer; no DbContext.
/// </summary>
public sealed class MailerAudienceSyncService(
    IMailerLiteService ml,
    IUserEmailService emails,
    IAuditLogService audit,
    IEnumerable<IMailerAudience> audiences,
    ILogger<MailerAudienceSyncService> logger) : IMailerAudienceSyncService
{
    private const string HumansGroupPrefix = "Humans - ";
    private const string JobName = nameof(MailerAudienceSyncService);

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

    public async Task<AudienceStats> ComputeStatsAsync(IMailerAudience audience, CancellationToken ct = default)
    {
        var snapshot = await BuildSnapshotAsync(ct);
        return await BuildStatsForAudienceAsync(audience, snapshot, ct);
    }

    public async Task<IReadOnlyList<AudienceStats>> ComputeAllStatsAsync(CancellationToken ct = default)
    {
        var snapshot = await BuildSnapshotAsync(ct);

        // Single audit-log read covering every audience; map by audience_key in memory.
        var auditEntries = await audit.GetFilteredEntriesAsync(
            actions: [AuditAction.MailerLiteAudienceSyncCompleted],
            limit: 200,
            ct: ct);

        var lastByKey = new Dictionary<string, AuditLogEntrySnapshot>(StringComparer.Ordinal);
        foreach (var e in auditEntries)
        {
            var key = TryExtractAudienceKey(e.Description);
            if (key is null) continue;
            if (!lastByKey.ContainsKey(key)) lastByKey[key] = e; // entries arrive newest-first
        }

        var rows = new List<AudienceStats>();
        foreach (var audience in audiences)
        {
            var stats = await BuildStatsForAudienceAsync(audience, snapshot, ct);
            if (lastByKey.TryGetValue(audience.Key, out var entry))
                stats = stats with
                {
                    LastSyncAt = entry.OccurredAt,
                    LastSyncSummary = entry.Description,
                };
            rows.Add(stats);
        }
        return rows;
    }

    public async Task<AudienceSyncResult> SyncAsync(
        IMailerAudience audience, Guid? actorUserId = null, CancellationToken ct = default)
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

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["audience_key"] = result.Key,
            ["group_id"] = result.GroupId,
            ["group_name"] = result.GroupName,
            ["candidates"] = result.Candidates,
            ["excluded_unsubscribed"] = result.ExcludedUnsubscribed,
            ["created"] = result.Created,
            ["assigned"] = result.Assigned,
            ["already_assigned"] = result.AlreadyAssigned,
            ["unassigned"] = result.Unassigned,
            ["errors"] = result.Errors,
        };
        var description = JsonSerializer.Serialize(metadata);

        if (actorUserId is Guid actor)
        {
            await audit.LogAsync(
                AuditAction.MailerLiteAudienceSyncCompleted,
                entityType: "MailerAudience", entityId: Guid.Empty,
                description: description,
                actorUserId: actor);
        }
        else
        {
            await audit.LogAsync(
                AuditAction.MailerLiteAudienceSyncCompleted,
                entityType: "MailerAudience", entityId: Guid.Empty,
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
        IMailerAudience audience, MlSnapshot snapshot, CancellationToken ct)
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

    private static string? TryExtractAudienceKey(string? description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        try
        {
            using var doc = JsonDocument.Parse(description);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("audience_key", out var keyEl)) return null;
            return keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeEmail(string email) =>
        email.Trim().ToLowerInvariant();

    private sealed record MlSnapshot(
        IReadOnlyDictionary<string, MailerLiteSubscriber> ByEmail,
        IReadOnlyList<MailerLiteGroup> Groups);
}
