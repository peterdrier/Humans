using System.Transactions;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Service for managing account merge requests. Business logic only — no
/// direct DbContext usage. All data access goes through repository
/// interfaces; cross-section operations (team membership, role assignments,
/// shift signups, notifications, etc.) route through the owning service.
/// </summary>
/// <remarks>
/// Moved from <c>Humans.Infrastructure.Services</c> to
/// <c>Humans.Application.Services.Profile</c> in PR #557 as the §15 Part 1
/// Profile-section cleanup. The owning Identity table
/// (<c>account_merge_requests</c>) is accessed via
/// <see cref="IAccountMergeRepository"/>; the UserEmail side uses
/// <see cref="IUserEmailRepository"/>.
/// <para>
/// As of the fold-into-target redesign (Phase 5), <see cref="AcceptAsync"/>
/// no longer anonymizes Profile rows or wipes source data. Instead each
/// section service exposes a <c>Reassign…ToUserAsync</c> method that re-FKs
/// its rows from source to target; <see cref="IUserService.AnonymizeForMergeAsync"/>
/// then tombstones the source <c>User</c> row by setting
/// <c>MergedToUserId</c> and <c>MergedAt</c> and locking out login.
/// </para>
/// </remarks>
public sealed class AccountMergeService : IAccountMergeService, IUserDataContributor
{
    private readonly IAccountMergeRepository _mergeRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly ILogger<AccountMergeService> _logger;
    private readonly IClock _clock;

    // Profile section
    private readonly IUserEmailService _userEmailService;
    private readonly IProfileService _profileService;
    private readonly IContactFieldService _contactFieldService;
    private readonly ICommunicationPreferenceService _communicationPreferenceService;

    // Cross-section
    private readonly IUserService _userService;
    private readonly ITicketSyncService _ticketSyncService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ITeamService _teamService;
    private readonly IShiftSignupService _shiftSignupService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IGeneralAvailabilityService _generalAvailabilityService;
    private readonly INotificationService _notificationService;
    private readonly ICampaignService _campaignService;
    private readonly ICampService _campService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IFeedbackService _feedbackService;

    public AccountMergeService(
        IAccountMergeRepository mergeRepository,
        IUserEmailRepository userEmailRepository,
        IAuditLogService auditLogService,
        IFullProfileInvalidator fullProfileInvalidator,
        ILogger<AccountMergeService> logger,
        IClock clock,
        IUserEmailService userEmailService,
        IProfileService profileService,
        IContactFieldService contactFieldService,
        ICommunicationPreferenceService communicationPreferenceService,
        IUserService userService,
        ITicketSyncService ticketSyncService,
        IRoleAssignmentService roleAssignmentService,
        ITeamService teamService,
        IShiftSignupService shiftSignupService,
        IShiftManagementService shiftManagementService,
        IGeneralAvailabilityService generalAvailabilityService,
        INotificationService notificationService,
        ICampaignService campaignService,
        ICampService campService,
        IApplicationDecisionService applicationDecisionService,
        IFeedbackService feedbackService)
    {
        _mergeRepository = mergeRepository;
        _userEmailRepository = userEmailRepository;
        _auditLogService = auditLogService;
        _fullProfileInvalidator = fullProfileInvalidator;
        _logger = logger;
        _clock = clock;

        _userEmailService = userEmailService;
        _profileService = profileService;
        _contactFieldService = contactFieldService;
        _communicationPreferenceService = communicationPreferenceService;

        _userService = userService;
        _ticketSyncService = ticketSyncService;
        _roleAssignmentService = roleAssignmentService;
        _teamService = teamService;
        _shiftSignupService = shiftSignupService;
        _shiftManagementService = shiftManagementService;
        _generalAvailabilityService = generalAvailabilityService;
        _notificationService = notificationService;
        _campaignService = campaignService;
        _campService = campService;
        _applicationDecisionService = applicationDecisionService;
        _feedbackService = feedbackService;
    }

    public Task<IReadOnlyList<AccountMergeRequest>> GetPendingRequestsAsync(CancellationToken ct = default) =>
        _mergeRepository.GetPendingAsync(ct);

    public Task<AccountMergeRequest?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _mergeRepository.GetByIdAsync(id, ct);

    public async Task AcceptAsync(
        Guid requestId, Guid adminUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var request = await _mergeRepository.GetByIdAsync(requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");

        if (request.Status != AccountMergeRequestStatus.Pending)
        {
            throw new InvalidOperationException("Merge request is not pending.");
        }

        var now = _clock.GetCurrentInstant();
        var sourceId = request.SourceUserId;
        var targetId = request.TargetUserId;

        _logger.LogInformation(
            "Admin {AdminId} accepting merge request {RequestId}: folding {SourceUserId} into {TargetUserId}",
            adminUserId, requestId, sourceId, targetId);

        try
        {
            // Ambient transaction so the cross-section writes below either
            // all commit or all roll back. Each section service / repository
            // creates its own short-lived DbContext via IDbContextFactory;
            // Npgsql enlists those connections in this scope automatically.
            // AsyncFlowOption.Enabled is mandatory for scopes containing
            // await — without it the transaction doesn't flow across
            // continuations.
            using (var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                // 1. Profile-section internal — re-FK the Profile sub-aggregates
                //    (UserEmail rows, Profile children, ContactFields,
                //    CommunicationPreferences) onto the target user.
                await _userEmailService.ReassignToUserAsync(sourceId, targetId, now, ct);
                await _profileService.ReassignSubAggregatesToUserAsync(sourceId, targetId, now, ct);
                await _contactFieldService.ReassignToUserAsync(sourceId, targetId, now, ct);
                await _communicationPreferenceService.ReassignToUserAsync(sourceId, targetId, now, ct);

                // 2. Cross-section — each owning service moves its rows from
                //    source to target. Order doesn't matter for correctness
                //    inside the same transaction, but we follow the order
                //    documented in the fold redesign spec (Phase 1–4 commits).
                await _userService.ReassignLoginsToUserAsync(sourceId, targetId, now, ct);
                await _userService.ReassignEventParticipationToUserAsync(sourceId, targetId, now, ct);
                await _ticketSyncService.ReassignToUserAsync(sourceId, targetId, now, ct);
                await _roleAssignmentService.ReassignToUserAsync(sourceId, targetId, now, ct);
                await _teamService.ReassignToUserAsync(sourceId, targetId, adminUserId, now, ct);
                await _shiftSignupService.ReassignToUserAsync(sourceId, targetId, now, ct);
                await _shiftManagementService.ReassignProfilesAndTagPrefsToUserAsync(sourceId, targetId, now, ct);
                await _generalAvailabilityService.ReassignToUserAsync(sourceId, targetId, now, ct);
                await _notificationService.ReassignRecipientsToUserAsync(sourceId, targetId, now, ct);
                await _campaignService.ReassignGrantsToUserAsync(sourceId, targetId, now, ct);
                await _campService.ReassignAssignmentsToUserAsync(sourceId, targetId, now, ct);
                await _applicationDecisionService.ReassignApplicationsToUserAsync(sourceId, targetId, now, ct);
                await _feedbackService.ReassignToUserAsync(sourceId, targetId, now, ct);

                // 3. Verify the pending email on the target. UserEmail.ReassignToUserAsync
                //    above moved any non-conflicting source emails over; the
                //    pending row was created on the target during request
                //    submission and still needs to flip to verified.
                var verified = await _userEmailRepository.MarkVerifiedAsync(request.PendingEmailId, now, ct);
                if (!verified)
                {
                    throw new InvalidOperationException(
                        $"Pending email {request.PendingEmailId} no longer exists. Cannot complete merge.");
                }

                // 4. Tombstone the source User row: sets MergedToUserId,
                //    MergedAt, and locks out login. Does NOT wipe data —
                //    the source row stays as a redirect for chain-follow
                //    reads (audit, consent, budget).
                await _userService.AnonymizeForMergeAsync(sourceId, targetId, now, ct);

                // 5. Mark the merge request as accepted.
                request.Status = AccountMergeRequestStatus.Accepted;
                request.ResolvedAt = now;
                request.ResolvedByUserId = adminUserId;
                request.AdminNotes = notes;
                await _mergeRepository.UpdateAsync(request, ct);

                // 6. Audit inside the same scope so a rolled-back merge
                //    doesn't leave a ghost audit row.
                await _auditLogService.LogAsync(
                    AuditAction.AccountMergeAccepted,
                    nameof(AccountMergeRequest), request.Id,
                    $"Folded source {sourceId} into target {targetId} — email: {request.Email}",
                    adminUserId,
                    relatedEntityId: targetId, relatedEntityType: nameof(User));

                scope.Complete();
            }

            // Cache invalidation runs AFTER the transaction commits so
            // cache-aside readers don't repopulate from rows that might
            // still roll back. Each owning section service strips its own
            // in-Reassign invalidator calls and we re-issue them here.
            // FullProfile covers UserEmail / ContactField /
            // CommunicationPreference (all Profile-section). Claims +
            // nav-badge cover RoleAssignment. Notification badge counts
            // cover NotificationRecipient. Team caches cover the
            // ITeamService.ReassignToUserAsync writes.
            await _fullProfileInvalidator.InvalidateAsync(sourceId, ct);
            await _fullProfileInvalidator.InvalidateAsync(targetId, ct);
            _teamService.RemoveMemberFromAllTeamsCache(sourceId);
            _roleAssignmentService.InvalidateClaimsCacheForUser(sourceId);
            _roleAssignmentService.InvalidateClaimsCacheForUser(targetId);
            _roleAssignmentService.InvalidateNavBadgeCache();
            _notificationService.InvalidateBadgeCachesForUsers([sourceId, targetId]);
        }
        finally
        {
            // The team service's Reassign call inside the scope mutates the
            // in-memory ActiveTeams cache immediately. On a rolled-back scope
            // those mutations would outlive the DB state, showing the
            // target joined to teams they don't actually belong to. Evict
            // the master cache so the next read repopulates from the
            // (possibly reverted) DB. Safe on the success path too — it
            // just costs one refetch.
            _teamService.InvalidateActiveTeamsCache();
        }
    }

    public async Task RejectAsync(
        Guid requestId, Guid adminUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var request = await _mergeRepository.GetByIdPlainAsync(requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");

        if (request.Status != AccountMergeRequestStatus.Pending)
        {
            throw new InvalidOperationException("Merge request is not pending.");
        }

        var now = _clock.GetCurrentInstant();

        // Ambient transaction so the pending-email delete and the request
        // status update commit together. Without this, a failed status write
        // would leave the request Pending with a dangling PendingEmailId,
        // blocking any later AcceptAsync call.
        using (var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            // Remove the pending (unverified) email from the target user's
            // account. MarkVerified may already be a no-op if the email
            // vanished; we just best-effort remove.
            await _userEmailRepository.RemoveByIdAsync(request.PendingEmailId, ct);

            request.Status = AccountMergeRequestStatus.Rejected;
            request.ResolvedAt = now;
            request.ResolvedByUserId = adminUserId;
            request.AdminNotes = notes;
            await _mergeRepository.UpdateAsync(request, ct);

            await _auditLogService.LogAsync(
                AuditAction.AccountMergeRejected,
                nameof(AccountMergeRequest), request.Id,
                $"Rejected merge request for email {request.Email} (target: {request.TargetUserId}, source: {request.SourceUserId})",
                adminUserId);

            scope.Complete();
        }

        // Invalidate the target's FullProfile so the removed pending email
        // disappears from the cached view. Runs after commit so cache-aside
        // reads don't repopulate from an uncommitted state.
        await _fullProfileInvalidator.InvalidateAsync(request.TargetUserId, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await _mergeRepository.GetForUserGdprAsync(userId, ct);

        var shaped = rows.Select(r => new
        {
            Status = r.Status,
            Role = r.IsTarget ? "Target" : "Source",
            CreatedAt = r.CreatedAt.ToInvariantInstantString(),
            ResolvedAt = r.ResolvedAt.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.AccountMergeRequests, shaped)];
    }

    // ---- Cross-section read helpers used by UserEmailService ----

    public Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default) =>
        _mergeRepository.GetPendingEmailIdsAsync(emailIds, ct);

    public Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default) =>
        _mergeRepository.HasPendingForUserAndEmailAsync(
            targetUserId, normalizedEmail, alternateEmail, ct);

    public Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default) =>
        _mergeRepository.HasPendingForEmailIdAsync(pendingEmailId, ct);

    public Task CreateAsync(AccountMergeRequest request, CancellationToken ct = default) =>
        _mergeRepository.AddAsync(request, ct);
}
