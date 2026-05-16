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

public sealed class AttendeeContactImportService : IAttendeeContactImportService
{
    private readonly ITicketRepository _ticketRepository;
    private readonly IUserEmailService _userEmails;
    private readonly IAccountProvisioningService _provisioning;
    private readonly IUserService _users;
    private readonly IShiftManagementService _shifts;
    private readonly ITicketQueryService _ticketQuery;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly ILogger<AttendeeContactImportService> _logger;

    public AttendeeContactImportService(
        ITicketRepository ticketRepository,
        IUserEmailService userEmails,
        IAccountProvisioningService provisioning,
        IUserService users,
        IShiftManagementService shifts,
        ITicketQueryService ticketQuery,
        IAuditLogService audit,
        IClock clock,
        ILogger<AttendeeContactImportService> logger)
    {
        _ticketRepository = ticketRepository;
        _userEmails = userEmails;
        _provisioning = provisioning;
        _users = users;
        _shifts = shifts;
        _ticketQuery = ticketQuery;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default)
    {
        var state = await _ticketRepository.GetSyncStateAsync(ct);
        var eventId = state?.VendorEventId;
        if (string.IsNullOrEmpty(eventId))
            throw new InvalidOperationException("No active vendor event id — sync has not run.");

        var unmatched = await _ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);
        var decisions = new List<AttendeeImportDecision>(unmatched.Count);

        foreach (var a in unmatched)
        {
            decisions.Add(await ClassifyAsync(a, ct));
        }

        return new AttendeeImportPlan(decisions, unmatched.Count);
    }

    public async Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var start = _clock.GetCurrentInstant();

        var state = await _ticketRepository.GetSyncStateAsync(ct);
        var eventId = state?.VendorEventId;
        if (string.IsNullOrEmpty(eventId))
            throw new InvalidOperationException("No active vendor event id — sync has not run.");

        // Re-query so plan/apply are stateless (a sync between plan and apply is tolerated).
        var freshUnmatched = await _ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);
        var freshById = freshUnmatched.ToDictionary(a => a.Id);

        var toUpsert = new List<TicketAttendee>();
        var newlyMatchedUserIds = new HashSet<Guid>();
        int attempted = 0, created = 0, attached = 0, replaced = 0,
            ambiguous = 0, noEmail = 0, vanished = 0, errors = 0;

        foreach (var d in plan.Decisions.Where(d => selectedAttendeeIds.Contains(d.AttendeeId)))
        {
            attempted++;

            if (!freshById.TryGetValue(d.AttendeeId, out var attendee))
            {
                vanished++;
                _logger.LogWarning(
                    "Attendee {AttendeeId} ({Email}) vanished between plan and apply",
                    d.AttendeeId, d.Email);
                continue;
            }

            // If the attendee's email changed between plan and apply, the plan's
            // decision (TargetUserId / UnverifiedEmailIdToDelete / etc.) was computed
            // against a stale email and may attach the wrong user. Treat as vanished.
            if (!string.Equals(attendee.AttendeeEmail, d.Email, StringComparison.OrdinalIgnoreCase))
            {
                vanished++;
                _logger.LogWarning(
                    "Attendee {AttendeeId} email drifted between plan ({PlanEmail}) and apply ({FreshEmail}); treating as vanished",
                    d.AttendeeId, d.Email, attendee.AttendeeEmail);
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
                        _logger.LogWarning(
                            "Attendee {AttendeeId} email {Email} verified by multiple users {UserIds}",
                            d.AttendeeId, d.Email, d.AmbiguousUserIds);
                        break;

                    case AttendeeImportOutcome.AttachVerified:
                        {
                            attendee.MatchedUserId = d.TargetUserId!.Value;
                            toUpsert.Add(attendee);
                            newlyMatchedUserIds.Add(d.TargetUserId.Value);
                            attached++;
                            break;
                        }

                    case AttendeeImportOutcome.DeleteUnverifiedThenCreate:
                        {
                            if (d.UnverifiedRowUserId is Guid uid &&
                                d.UnverifiedEmailIdToDelete is Guid eid)
                            {
                                await _userEmails.DeleteEmailAsync(uid, eid, ct);
                            }
                            var (newUser, wasCreated) = await _provisioning.FindOrCreateUserByEmailAsync(
                                d.Email!, d.AttendeeName, ContactSource.TicketTailor, ct);
                            attendee.MatchedUserId = newUser.Id;
                            toUpsert.Add(attendee);
                            newlyMatchedUserIds.Add(newUser.Id);
                            if (wasCreated) created++;
                            replaced++;
                            break;
                        }

                    case AttendeeImportOutcome.CreateNewUser:
                        {
                            var (newUser, wasCreated) = await _provisioning.FindOrCreateUserByEmailAsync(
                                d.Email!, d.AttendeeName, ContactSource.TicketTailor, ct);
                            attendee.MatchedUserId = newUser.Id;
                            toUpsert.Add(attendee);
                            newlyMatchedUserIds.Add(newUser.Id);
                            if (wasCreated) created++;
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                _logger.LogError(ex,
                    "Attendee contact import failed for {AttendeeId} ({Email})",
                    d.AttendeeId, d.Email);
            }
        }

        if (toUpsert.Count > 0)
        {
            await _ticketRepository.UpsertAttendeesAsync(toUpsert, ct);
        }

        // Evict before the participation loop — if SetParticipationFromTicketSyncAsync throws,
        // the attendee mutation above must still invalidate ticket caches.
        _ticketQuery.InvalidateAfterContactImport();

        var active = await _shifts.GetActiveAsync();
        if (active is not null && newlyMatchedUserIds.Count > 0)
        {
            foreach (var userId in newlyMatchedUserIds)
            {
                await _users.SetParticipationFromTicketSyncAsync(
                    userId, active.Year, ParticipationStatus.Ticketed, ct);
            }
        }

        var elapsed = _clock.GetCurrentInstant() - start;
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

        await _audit.LogAsync(
            AuditAction.TicketContactsImported,
            "Tickets", Guid.Empty,
            description: result.FormatSummary(),
            actorUserId: actorUserId);

        return result;
    }

    private async Task<AttendeeImportDecision> ClassifyAsync(TicketAttendee a, CancellationToken ct)
    {
        var name = ResolveDisplayName(a);

        if (string.IsNullOrWhiteSpace(a.AttendeeEmail))
        {
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.SkipNoEmail,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null);
        }

        var verifiedUserIds = await _userEmails.GetDistinctVerifiedUserIdsAsync(a.AttendeeEmail, ct);

        if (verifiedUserIds.Count > 1)
        {
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.AmbiguousMultipleVerified,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: verifiedUserIds);
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
                AmbiguousUserIds: null);
        }

        var existingRow = await _userEmails.FindAnyEmailRowByAddressAsync(a.AttendeeEmail, ct);
        if (existingRow is var (uid, emailId))
        {
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: emailId,
                UnverifiedRowUserId: uid,
                AmbiguousUserIds: null);
        }

        return new AttendeeImportDecision(
            a.Id, a.AttendeeEmail, name, a.VendorTicketId,
            AttendeeImportOutcome.CreateNewUser,
            TargetUserId: null,
            UnverifiedEmailIdToDelete: null,
            UnverifiedRowUserId: null,
            AmbiguousUserIds: null);
    }

    private async Task<Guid> ResolveTombstoneAsync(Guid userId, CancellationToken ct)
    {
        var visited = new HashSet<Guid> { userId };
        var current = userId;
        while (true)
        {
            var user = await _users.GetByIdAsync(current, ct);
            if (user?.MergedToUserId is not Guid next) return current;
            if (!visited.Add(next)) return current;
            current = next;
        }
    }

    private static string? ResolveDisplayName(TicketAttendee a) =>
        string.IsNullOrWhiteSpace(a.AttendeeName) ? null : a.AttendeeName.Trim();
}
