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
        var eventId = await RequireActiveVendorEventIdAsync(ct);

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
        var freshById = await LoadFreshUnmatchedAttendeesByIdAsync(eventId, ct);
        var importState = new AttendeeImportApplyState();

        foreach (var d in plan.Decisions.Where(d => selectedAttendeeIds.Contains(d.AttendeeId)))
        {
            importState.Attempted++;

            var resolved = ResolveFreshAttendeeGroup(d, freshById);

            if (resolved.Count == 0)
            {
                importState.Vanished++;
                continue;
            }

            try
            {
                await ApplyResolvedImportDecisionAsync(d, resolved, importState, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                importState.Errors++;
                logger.LogError(ex,
                    "Attendee contact import failed for {AttendeeId} ({Email})",
                    d.AttendeeId, d.Email);
            }
        }

        if (importState.ToUpsert.Count > 0)
        {
            await ticketRepository.UpsertAttendeesAsync(importState.ToUpsert, ct);
        }

        // Evict before participation loop so attendee mutation always invalidates caches.
        ticketCacheInvalidator.InvalidateAfterContactImport();

        var active = await shifts.GetActiveAsync();
        if (active is not null && importState.NewlyMatchedUserIds.Count > 0)
        {
            foreach (var userId in importState.NewlyMatchedUserIds)
            {
                await users.SetParticipationFromTicketSyncAsync(
                    userId, active.Year, ParticipationStatus.Ticketed, checkedInAt: null, ct);
            }
        }

        var result = BuildImportResult(importState, start);

        await audit.LogAsync(
            AuditAction.TicketContactsImported,
            "Tickets", Guid.Empty,
            description: result.FormatSummary(),
            actorUserId: actorUserId);

        return result;
    }

    private async Task<string> RequireActiveVendorEventIdAsync(CancellationToken ct)
    {
        var state = await ticketRepository.GetSyncStateAsync(ct);
        return string.IsNullOrEmpty(state?.VendorEventId)
            ? throw new InvalidOperationException("No active vendor event id — sync has not run.")
            : state.VendorEventId;
    }

    private async Task<Dictionary<Guid, TicketAttendee>> LoadFreshUnmatchedAttendeesByIdAsync(
        string eventId,
        CancellationToken ct)
    {
        var freshUnmatched = await ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);
        return freshUnmatched.ToDictionary(a => a.Id);
    }

    private List<TicketAttendee> ResolveFreshAttendeeGroup(
        AttendeeImportDecision decision,
        IReadOnlyDictionary<Guid, TicketAttendee> freshById)
    {
        var groupIds = new List<Guid>(1 + (decision.AdditionalAttendeeIds?.Count ?? 0))
            { decision.AttendeeId };
        if (decision.AdditionalAttendeeIds is { Count: > 0 } more)
            groupIds.AddRange(more);

        var resolved = new List<TicketAttendee>();
        foreach (var groupId in groupIds)
        {
            if (!freshById.TryGetValue(groupId, out var attendee))
            {
                logger.LogWarning(
                    "Attendee {AttendeeId} ({Email}) vanished between plan and apply",
                    groupId, decision.Email);
                continue;
            }

            if (!string.Equals(
                    attendee.AttendeeEmail?.Trim(),
                    decision.Email?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Attendee {AttendeeId} email drifted between plan ({PlanEmail}) and apply ({FreshEmail}); skipping",
                    groupId, decision.Email, attendee.AttendeeEmail);
                continue;
            }

            resolved.Add(attendee);
        }

        return resolved;
    }

    private async Task ApplyResolvedImportDecisionAsync(
        AttendeeImportDecision decision,
        IReadOnlyList<TicketAttendee> resolved,
        AttendeeImportApplyState state,
        CancellationToken ct)
    {
        switch (decision.Outcome)
        {
            case AttendeeImportOutcome.SkipNoEmail:
                state.NoEmail++;
                return;

            case AttendeeImportOutcome.SkipVoided:
                return;

            case AttendeeImportOutcome.AmbiguousMultipleVerified:
                state.Ambiguous++;
                logger.LogWarning(
                    "Attendee {AttendeeId} email {Email} verified by multiple users {UserIds}",
                    decision.AttendeeId, decision.Email, decision.AmbiguousUserIds);
                return;

            case AttendeeImportOutcome.AttachVerified:
                AttachResolvedAttendees(resolved, decision.TargetUserId!.Value, state);
                state.Attached++;
                return;

            case AttendeeImportOutcome.DeleteUnverifiedThenCreate:
                if (decision.UnverifiedRowUserId is Guid uid &&
                    decision.UnverifiedEmailIdToDelete is Guid eid)
                {
                    await userEmails.DeleteEmailAsync(uid, eid, ct);
                }

                var replacement = await provisioning.FindOrCreateUserByEmailAsync(
                    decision.Email!, decision.AttendeeName, ContactSource.TicketTailor, ct);
                AttachResolvedAttendees(resolved, replacement.User.Id, state);
                if (replacement.Created) state.Created++;
                state.Replaced++;
                return;

            case AttendeeImportOutcome.CreateNewUser:
                var createdUser = await provisioning.FindOrCreateUserByEmailAsync(
                    decision.Email!, decision.AttendeeName, ContactSource.TicketTailor, ct);
                AttachResolvedAttendees(resolved, createdUser.User.Id, state);
                if (createdUser.Created) state.Created++;
                return;
        }
    }

    private static void AttachResolvedAttendees(
        IEnumerable<TicketAttendee> resolved,
        Guid userId,
        AttendeeImportApplyState state)
    {
        foreach (var attendee in resolved)
        {
            attendee.MatchedUserId = userId;
            state.ToUpsert.Add(attendee);
        }

        state.NewlyMatchedUserIds.Add(userId);
    }

    private AttendeeImportResult BuildImportResult(AttendeeImportApplyState state, Instant start)
        => new(
            TotalAttempted: state.Attempted,
            UsersCreated: state.Created,
            AttachedToExistingVerified: state.Attached,
            UnverifiedRowsDeletedAndUserCreated: state.Replaced,
            AmbiguousSkipped: state.Ambiguous,
            NoEmailSkipped: state.NoEmail,
            VanishedBetweenPlanAndApply: state.Vanished,
            Errors: state.Errors,
            Elapsed: clock.GetCurrentInstant() - start);

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

    private sealed class AttendeeImportApplyState
    {
        public List<TicketAttendee> ToUpsert { get; } = [];
        public HashSet<Guid> NewlyMatchedUserIds { get; } = [];
        public int Attempted { get; set; }
        public int Created { get; set; }
        public int Attached { get; set; }
        public int Replaced { get; set; }
        public int Ambiguous { get; set; }
        public int NoEmail { get; set; }
        public int Vanished { get; set; }
        public int Errors { get; set; }
    }
}
