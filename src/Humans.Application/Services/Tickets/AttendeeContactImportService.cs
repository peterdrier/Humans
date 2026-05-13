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

    public Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Filled in by Task 11");

    private Task<AttendeeImportDecision> ClassifyAsync(TicketAttendee a, CancellationToken ct)
    {
        var name = ResolveDisplayName(a);

        if (string.IsNullOrWhiteSpace(a.AttendeeEmail))
        {
            return Task.FromResult(new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.SkipNoEmail,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null));
        }

        // Remaining classifications filled in by Tasks 9–10.
        throw new NotSupportedException("Branches filled in by Tasks 9–10");
    }

    private static string? ResolveDisplayName(TicketAttendee a) =>
        string.IsNullOrWhiteSpace(a.AttendeeName) ? null : a.AttendeeName.Trim();
}
