using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Tickets;

public sealed class AttendeeContactImportService(
    ITicketRepository ticketRepository,
    IUserEmailService userEmails,
    IAccountProvisioningService provisioning,
    IUserService users,
    IShiftManagementService shifts,
    ITicketCacheInvalidator ticketCacheInvalidator,
    IAuditLogService audit,
    IClock clock,
    ILogger<AttendeeContactImportService> logger) : IAttendeeContactImportService
{
    public async Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default)
    {
        var state = await ticketRepository.GetSyncStateAsync(ct);
        var eventId = state?.VendorEventId;
        if (string.IsNullOrEmpty(eventId))
            throw new InvalidOperationException("No active vendor event id — sync has not run.");

        var unmatched = await ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);
        var decisions = new List<AttendeeImportDecision>();

        // No email = one decision each (ungroupable).
        foreach (var a in unmatched.Where(a => string.IsNullOrWhiteSpace(a.AttendeeEmail)))
        {
            decisions.Add(await ClassifyAsync(a, [], [], ct));
        }

        // Group by normalized email so one buyer = one decision.
        var grouped = unmatched
            .Where(a => !string.IsNullOrWhiteSpace(a.AttendeeEmail))
            .GroupBy(a => a.AttendeeEmail!.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            // Stable lead across GET/POST — repo has no ORDER BY.
            var members = group.OrderBy(m => m.VendorTicketId, StringComparer.Ordinal).ToList();
            var lead = members[0];
            var additional = members.Skip(1).Select(m => m.Id).ToList();
            var observed = members
                .Select(ResolveDisplayName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            decisions.Add(await ClassifyAsync(lead, additional, observed, ct));
        }

        return new AttendeeImportPlan(decisions, unmatched.Count);
    }

    public async Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var start = clock.GetCurrentInstant();

        var state = await ticketRepository.GetSyncStateAsync(ct);
        var eventId = state?.VendorEventId;
        if (string.IsNullOrEmpty(eventId))
            throw new InvalidOperationException("No active vendor event id — sync has not run.");

        // Re-query so plan/apply are stateless (a sync between plan and apply is tolerated).
        var freshUnmatched = await ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);
        var freshById = freshUnmatched.ToDictionary(a => a.Id);

        var toUpsert = new List<TicketAttendee>();
        var newlyMatchedUserIds = new HashSet<Guid>();
        int attempted = 0, created = 0, attached = 0, replaced = 0,
            ambiguous = 0, noEmail = 0, vanished = 0, errors = 0;

        foreach (var d in plan.Decisions.Where(d => selectedAttendeeIds.Contains(d.AttendeeId)))
        {
            attempted++;

            var groupIds = new List<Guid>(1 + (d.AdditionalAttendeeIds?.Count ?? 0))
                { d.AttendeeId };
            if (d.AdditionalAttendeeIds is { Count: > 0 } more) groupIds.AddRange(more);

            // Tolerate per-attendee drift between plan and apply (lead must remain).
            var resolved = new List<TicketAttendee>();
            foreach (var gid in groupIds)
            {
                if (!freshById.TryGetValue(gid, out var ga))
                {
                    logger.LogWarning(
                        "Attendee {AttendeeId} ({Email}) vanished between plan and apply",
                        gid, d.Email);
                    continue;
                }
                // Trim+OrdinalIgnoreCase to match plan-time grouping.
                if (!string.Equals(ga.AttendeeEmail?.Trim(), d.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Attendee {AttendeeId} email drifted between plan ({PlanEmail}) and apply ({FreshEmail}); skipping",
                        gid, d.Email, ga.AttendeeEmail);
                    continue;
                }
                resolved.Add(ga);
            }

            if (resolved.Count == 0)
            {
                vanished++;
                continue;
            }

            try
            {
                switch (d.Outcome)
                {
                    case AttendeeImportOutcome.SkipNoEmail:
                        noEmail++;
                        break;

                    case AttendeeImportOutcome.SkipVoided:
                        break;

                    case AttendeeImportOutcome.AmbiguousMultipleVerified:
                        ambiguous++;
                        logger.LogWarning(
                            "Attendee {AttendeeId} email {Email} verified by multiple users {UserIds}",
                            d.AttendeeId, d.Email, d.AmbiguousUserIds);
                        break;

                    case AttendeeImportOutcome.AttachVerified:
                        {
                            var targetUserId = d.TargetUserId!.Value;
                            foreach (var ga in resolved)
                            {
                                ga.MatchedUserId = targetUserId;
                                toUpsert.Add(ga);
                            }
                            newlyMatchedUserIds.Add(targetUserId);
                            attached++;
                            break;
                        }

                    case AttendeeImportOutcome.DeleteUnverifiedThenCreate:
                        {
                            if (d.UnverifiedRowUserId is Guid uid &&
                                d.UnverifiedEmailIdToDelete is Guid eid)
                            {
                                await userEmails.DeleteEmailAsync(uid, eid, ct);
                            }
                            var (newUser, wasCreated) = await provisioning.FindOrCreateUserByEmailAsync(
                                d.Email!, d.AttendeeName, ContactSource.TicketTailor, ct);
                            foreach (var ga in resolved)
                            {
                                ga.MatchedUserId = newUser.Id;
                                toUpsert.Add(ga);
                            }
                            newlyMatchedUserIds.Add(newUser.Id);
                            if (wasCreated) created++;
                            replaced++;
                            break;
                        }

                    case AttendeeImportOutcome.CreateNewUser:
                        {
                            var (newUser, wasCreated) = await provisioning.FindOrCreateUserByEmailAsync(
                                d.Email!, d.AttendeeName, ContactSource.TicketTailor, ct);
                            foreach (var ga in resolved)
                            {
                                ga.MatchedUserId = newUser.Id;
                                toUpsert.Add(ga);
                            }
                            newlyMatchedUserIds.Add(newUser.Id);
                            if (wasCreated) created++;
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                logger.LogError(ex,
                    "Attendee contact import failed for {AttendeeId} ({Email})",
                    d.AttendeeId, d.Email);
            }
        }

        if (toUpsert.Count > 0)
        {
            await ticketRepository.UpsertAttendeesAsync(toUpsert, ct);
        }

        // Evict before participation loop so attendee mutation always invalidates caches.
        ticketCacheInvalidator.InvalidateAfterContactImport();

        var active = await shifts.GetActiveAsync();
        if (active is not null && newlyMatchedUserIds.Count > 0)
        {
            foreach (var userId in newlyMatchedUserIds)
            {
                await users.SetParticipationFromTicketSyncAsync(
                    userId, active.Year, ParticipationStatus.Ticketed, checkedInAt: null, ct);
            }
        }

        var elapsed = clock.GetCurrentInstant() - start;
        var result = new AttendeeImportResult(
            TotalAttempted: attempted,
            UsersCreated: created,
            AttachedToExistingVerified: attached,
            UnverifiedRowsDeletedAndUserCreated: replaced,
            AmbiguousSkipped: ambiguous,
            NoEmailSkipped: noEmail,
            VanishedBetweenPlanAndApply: vanished,
            Errors: errors,
            Elapsed: elapsed);

        await audit.LogAsync(
            AuditAction.TicketContactsImported,
            "Tickets", Guid.Empty,
            description: result.FormatSummary(),
            actorUserId: actorUserId);

        return result;
    }

    private async Task<AttendeeImportDecision> ClassifyAsync(
        TicketAttendee a,
        IReadOnlyList<Guid> additionalAttendeeIds,
        IReadOnlyList<string> observedNames,
        CancellationToken ct)
    {
        var name = ResolveDisplayName(a);
        var addl = additionalAttendeeIds.Count > 0 ? additionalAttendeeIds : null;
        var names = observedNames.Count > 0 ? observedNames : null;

        if (string.IsNullOrWhiteSpace(a.AttendeeEmail))
        {
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.SkipNoEmail,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null,
                AdditionalAttendeeIds: addl,
                ObservedNames: names);
        }

        var verifiedUserIds = await userEmails.GetDistinctVerifiedUserIdsAsync(a.AttendeeEmail, ct);

        if (verifiedUserIds.Count > 1)
        {
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.AmbiguousMultipleVerified,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: verifiedUserIds,
                AdditionalAttendeeIds: addl,
                ObservedNames: names);
        }

        if (verifiedUserIds.Count == 1)
        {
            var liveTarget = await ResolveTombstoneAsync(verifiedUserIds[0], ct);
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.AttachVerified,
                TargetUserId: liveTarget,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null,
                AdditionalAttendeeIds: addl,
                ObservedNames: names);
        }

        var existingRow = await userEmails.FindAnyEmailRowByAddressAsync(a.AttendeeEmail, ct);
        if (existingRow is var (uid, emailId))
        {
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: emailId,
                UnverifiedRowUserId: uid,
                AmbiguousUserIds: null,
                AdditionalAttendeeIds: addl,
                ObservedNames: names);
        }

        return new AttendeeImportDecision(
            a.Id, a.AttendeeEmail, name, a.VendorTicketId,
            AttendeeImportOutcome.CreateNewUser,
            TargetUserId: null,
            UnverifiedEmailIdToDelete: null,
            UnverifiedRowUserId: null,
            AmbiguousUserIds: null,
            AdditionalAttendeeIds: addl,
            ObservedNames: names);
    }

    private async Task<Guid> ResolveTombstoneAsync(Guid userId, CancellationToken ct)
    {
        var visited = new HashSet<Guid> { userId };
        var current = userId;
        while (true)
        {
            var user = await users.GetUserInfoAsync(current, ct);
            if (user?.MergedToUserId is not Guid next) return current;
            if (!visited.Add(next)) return current;
            current = next;
        }
    }

    private static string? ResolveDisplayName(TicketAttendee a) =>
        string.IsNullOrWhiteSpace(a.AttendeeName) ? null : a.AttendeeName.Trim();
}
