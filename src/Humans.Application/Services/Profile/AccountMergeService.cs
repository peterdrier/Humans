using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Service for managing account merge requests. Business logic only — no
/// direct DbContext usage. All data access goes through repository
/// interfaces; cross-section operations (team membership, role assignments)
/// route through the owning service.
/// </summary>
/// <remarks>
/// Moved from <c>Humans.Infrastructure.Services</c> to
/// <c>Humans.Application.Services.Profile</c> in PR #557 as the §15 Part 1
/// Profile-section cleanup. The owning Identity table
/// (<c>account_merge_requests</c>) is accessed via
/// <see cref="IAccountMergeRepository"/>; the UserEmail side uses
/// <see cref="IUserEmailRepository"/>; User and Profile anonymization use the
/// corresponding repositories' dedicated methods.
/// </remarks>
public sealed class AccountMergeService : IAccountMergeService, IUserDataContributor
{
    private readonly IAccountMergeRepository _mergeRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IUserRepository _userRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ILogger<AccountMergeService> _logger;
    private readonly IClock _clock;

    public AccountMergeService(
        IAccountMergeRepository mergeRepository,
        IUserEmailRepository userEmailRepository,
        IUserRepository userRepository,
        IProfileRepository profileRepository,
        IAuditLogService auditLogService,
        IFullProfileInvalidator fullProfileInvalidator,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        ILogger<AccountMergeService> logger,
        IClock clock)
    {
        _mergeRepository = mergeRepository;
        _userEmailRepository = userEmailRepository;
        _userRepository = userRepository;
        _profileRepository = profileRepository;
        _auditLogService = auditLogService;
        _fullProfileInvalidator = fullProfileInvalidator;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _logger = logger;
        _clock = clock;
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
        var sourceUserId = request.SourceUserId;
        var targetUserId = request.TargetUserId;
        var sourceDisplayName = request.SourceUser.DisplayName;
        var targetDisplayName = request.TargetUser.DisplayName;

        _logger.LogInformation(
            "Admin {AdminId} accepting merge request {RequestId}: merging {SourceUserId} ({SourceName}) into {TargetUserId} ({TargetName})",
            adminUserId, requestId, sourceUserId, sourceDisplayName, targetUserId, targetDisplayName);

        // 1. Add target to any non-system teams the source is in (via service)
        //    System teams (e.g. Volunteers) are managed automatically — skip them.
        var sourceTeams = await _teamService.GetUserTeamsAsync(sourceUserId, ct);
        var targetTeams = await _teamService.GetUserTeamsAsync(targetUserId, ct);
        var targetTeamIds = targetTeams.Select(m => m.TeamId).ToHashSet();

        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam && !targetTeamIds.Contains(m.TeamId)))
        {
            await _teamService.AddMemberToTeamAsync(membership.TeamId, targetUserId, adminUserId, ct);
        }

        // 2. Remove source from all non-system teams (via service)
        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam))
        {
            await _teamService.RemoveMemberAsync(membership.TeamId, sourceUserId, adminUserId, ct);
        }

        // 3. End source's active role assignments (system sync will re-evaluate)
        await _roleAssignmentService.RevokeAllActiveAsync(sourceUserId, ct);

        // 4. Remove source's external logins (prevents lockout when logging in
        //    with a secondary email that was on the source account)
        await _userRepository.RemoveExternalLoginsAsync(sourceUserId, ct);

        // 5. Delete source's email rows (must happen before verifying the
        //    pending email to avoid unique constraint violation)
        await _userEmailRepository.RemoveAllForUserAndSaveAsync(sourceUserId, ct);

        // 6. Verify the pending email on the primary account
        var verified = await _userEmailRepository.MarkVerifiedAsync(request.PendingEmailId, now, ct);
        if (!verified)
        {
            throw new InvalidOperationException(
                $"Pending email {request.PendingEmailId} no longer exists. Cannot complete merge.");
        }

        // 7. Anonymize the source account (profile first, then user)
        await _profileRepository.AnonymizeForMergeByUserIdAsync(sourceUserId, ct);
        await _userRepository.AnonymizeForMergeAsync(sourceUserId, ct);

        // 8. Mark the merge request as accepted
        request.Status = AccountMergeRequestStatus.Accepted;
        request.ResolvedAt = now;
        request.ResolvedByUserId = adminUserId;
        request.AdminNotes = notes;
        await _mergeRepository.UpdateAsync(request, ct);

        // 9. Audit log
        await _auditLogService.LogAsync(
            AuditAction.AccountMergeAccepted,
            nameof(AccountMergeRequest), request.Id,
            $"Merged account (source: {sourceUserId}) into (target: {targetUserId}) — email: {request.Email}",
            adminUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        // Invalidate caches for both the source (anonymized) and target
        // (gained a verified email) so the FullProfile decorator reloads.
        await _fullProfileInvalidator.InvalidateAsync(sourceUserId, ct);
        await _fullProfileInvalidator.InvalidateAsync(targetUserId, ct);
        _teamService.RemoveMemberFromAllTeamsCache(sourceUserId);
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

        // Remove the pending (unverified) email from the target user's account.
        // MarkVerified may already be a no-op if the email vanished; we just
        // best-effort remove.
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

        // Invalidate the target's FullProfile so the removed pending email
        // disappears from the cached view.
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
