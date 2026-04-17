using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for managing account merge requests.
/// When accepted, migrates data from the source account to the target account
/// and archives (anonymizes) the source account.
/// </summary>
public class AccountMergeService : IAccountMergeService, IUserDataContributor
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IUserEmailService _userEmailService;
    private readonly ILogger<AccountMergeService> _logger;
    private readonly IClock _clock;

    public AccountMergeService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IProfileService profileService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IUserEmailService userEmailService,
        ILogger<AccountMergeService> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _profileService = profileService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _userEmailService = userEmailService;
        _logger = logger;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AccountMergeRequest>> GetPendingRequestsAsync(CancellationToken ct = default)
    {
        return await _dbContext.AccountMergeRequests
            .AsNoTracking()
            .Include(r => r.TargetUser)
            .Include(r => r.SourceUser)
            .Where(r => r.Status == AccountMergeRequestStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<AccountMergeRequest?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _dbContext.AccountMergeRequests
            .Include(r => r.TargetUser)
            .Include(r => r.SourceUser)
            .Include(r => r.ResolvedByUser)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task AcceptAsync(
        Guid requestId, Guid adminUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var request = await _dbContext.AccountMergeRequests
            .Include(r => r.TargetUser)
            .Include(r => r.SourceUser)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");

        if (request.Status != AccountMergeRequestStatus.Pending)
        {
            throw new InvalidOperationException("Merge request is not pending.");
        }

        var now = _clock.GetCurrentInstant();
        var sourceUser = request.SourceUser;
        var targetUser = request.TargetUser;
        var sourceDisplayName = sourceUser.DisplayName;

        _logger.LogInformation(
            "Admin {AdminId} accepting merge request {RequestId}: merging {SourceUserId} ({SourceName}) into {TargetUserId} ({TargetName})",
            adminUserId, requestId, sourceUser.Id, sourceDisplayName, targetUser.Id, targetUser.DisplayName);

        // 1. Add primary to any non-system teams the duplicate is in (via service)
        //    System teams (e.g. Volunteers) are managed automatically — skip them.
        var sourceTeams = await _teamService.GetUserTeamsAsync(sourceUser.Id, ct);
        var targetTeamIds = (await _teamService.GetUserTeamsAsync(targetUser.Id, ct))
            .Select(m => m.TeamId).ToHashSet();

        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam && !targetTeamIds.Contains(m.TeamId)))
        {
            await _teamService.AddMemberToTeamAsync(membership.TeamId, targetUser.Id, adminUserId, ct);
        }

        // 2. Remove duplicate from all non-system teams (via service)
        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam))
        {
            await _teamService.RemoveMemberAsync(membership.TeamId, sourceUser.Id, adminUserId, ct);
        }

        // 3. End duplicate's active role assignments (system sync will re-evaluate)
        await _roleAssignmentService.RevokeAllActiveAsync(sourceUser.Id, ct);

        // 4. Remove duplicate's external logins (prevents lockout when logging in
        //    with a secondary email that was on the source account)
        var sourceLogins = await _dbContext.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == sourceUser.Id)
            .ToListAsync(ct);
        _dbContext.Set<IdentityUserLogin<Guid>>().RemoveRange(sourceLogins);

        // 5. Delete duplicate's email rows (must happen before verifying
        //    the pending email to avoid unique constraint violation)
        await _userEmailService.RemoveAllEmailsAsync(sourceUser.Id, ct);
        await _dbContext.SaveChangesAsync(ct);

        // 6. Verify the pending email on the primary account
        var pendingEmail = await _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.Id == request.PendingEmailId, ct)
            ?? throw new InvalidOperationException(
                $"Pending email {request.PendingEmailId} no longer exists. Cannot complete merge.");
        pendingEmail.IsVerified = true;
        pendingEmail.UpdatedAt = now;

        // 7. Anonymize the duplicate account
        await AnonymizeSourceAccountAsync(sourceUser, now, ct);

        // 8. Mark the merge request as accepted
        request.Status = AccountMergeRequestStatus.Accepted;
        request.ResolvedAt = now;
        request.ResolvedByUserId = adminUserId;
        request.AdminNotes = notes;

        // 9. Audit log
        await _auditLogService.LogAsync(
            AuditAction.AccountMergeAccepted,
            nameof(AccountMergeRequest), request.Id,
            $"Merged account (source: {sourceUser.Id}) into (target: {targetUser.Id}) — email: {request.Email}",
            adminUserId,
            relatedEntityId: targetUser.Id, relatedEntityType: nameof(User));

        await _dbContext.SaveChangesAsync(ct);

        // Invalidate caches
        await _profileService.InvalidateCacheAsync(sourceUser.Id, ct);
        _teamService.RemoveMemberFromAllTeamsCache(sourceUser.Id);
    }

    public async Task RejectAsync(
        Guid requestId, Guid adminUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var request = await _dbContext.AccountMergeRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");

        if (request.Status != AccountMergeRequestStatus.Pending)
        {
            throw new InvalidOperationException("Merge request is not pending.");
        }

        var now = _clock.GetCurrentInstant();

        // Remove the pending (unverified) email from the target user's account
        var pendingEmail = await _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.Id == request.PendingEmailId, ct);

        if (pendingEmail != null)
        {
            _dbContext.UserEmails.Remove(pendingEmail);
        }

        request.Status = AccountMergeRequestStatus.Rejected;
        request.ResolvedAt = now;
        request.ResolvedByUserId = adminUserId;
        request.AdminNotes = notes;

        await _auditLogService.LogAsync(
            AuditAction.AccountMergeRejected,
            nameof(AccountMergeRequest), request.Id,
            $"Rejected merge request for email {request.Email} (target: {request.TargetUserId}, source: {request.SourceUserId})",
            adminUserId);

        await _dbContext.SaveChangesAsync(ct);
    }

    private async Task AnonymizeSourceAccountAsync(User sourceUser, Instant now, CancellationToken ct)
    {
        var anonymizedId = $"merged-{sourceUser.Id:N}";

        sourceUser.DisplayName = "Merged User";
        sourceUser.Email = $"{anonymizedId}@merged.local";
        sourceUser.NormalizedEmail = sourceUser.Email.ToUpperInvariant();
        sourceUser.UserName = anonymizedId;
        sourceUser.NormalizedUserName = anonymizedId.ToUpperInvariant();
        sourceUser.ProfilePictureUrl = null;
        sourceUser.PhoneNumber = null;
        sourceUser.PhoneNumberConfirmed = false;

        // Clear deletion request fields
        sourceUser.DeletionRequestedAt = null;
        sourceUser.DeletionScheduledFor = null;

        // Disable login
        sourceUser.LockoutEnabled = true;
        sourceUser.LockoutEnd = DateTimeOffset.MaxValue;
        sourceUser.SecurityStamp = Guid.NewGuid().ToString();

        // Clear iCal token
        sourceUser.ICalToken = null;

        // Anonymize profile if exists
        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == sourceUser.Id, ct);

        if (profile != null)
        {
            profile.FirstName = "Merged";
            profile.LastName = "User";
            profile.BurnerName = string.Empty;
            profile.Bio = null;
            profile.City = null;
            profile.CountryCode = null;
            profile.Latitude = null;
            profile.Longitude = null;
            profile.PlaceId = null;
            profile.AdminNotes = null;
            profile.Pronouns = null;
            profile.DateOfBirth = null;
            profile.ProfilePictureData = null;
            profile.ProfilePictureContentType = null;
            profile.EmergencyContactName = null;
            profile.EmergencyContactPhone = null;
            profile.EmergencyContactRelationship = null;
            profile.ContributionInterests = null;
            profile.BoardNotes = null;

            // Remove contact fields and volunteer history
            var contactFields = await _dbContext.ContactFields
                .Where(cf => cf.ProfileId == profile.Id)
                .ToListAsync(ct);
            _dbContext.ContactFields.RemoveRange(contactFields);

            var volunteerHistory = await _dbContext.VolunteerHistoryEntries
                .Where(vh => vh.ProfileId == profile.Id)
                .ToListAsync(ct);
            _dbContext.VolunteerHistoryEntries.RemoveRange(volunteerHistory);
        }

        // Note: Consent records are immutable (INSERT-only), kept for GDPR audit trail.
        // Applications are kept as historical records, linked to the anonymized user.
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var requests = await _dbContext.AccountMergeRequests
            .AsNoTracking()
            .Where(amr => amr.TargetUserId == userId || amr.SourceUserId == userId)
            .OrderByDescending(amr => amr.CreatedAt)
            .Select(amr => new
            {
                amr.Status,
                Role = amr.TargetUserId == userId ? "Target" : "Source",
                CreatedAt = amr.CreatedAt,
                ResolvedAt = amr.ResolvedAt
            })
            .ToListAsync(ct);

        var shaped = requests.Select(r => new
        {
            r.Status,
            r.Role,
            CreatedAt = r.CreatedAt.ToInvariantInstantString(),
            ResolvedAt = r.ResolvedAt.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.AccountMergeRequests, shaped)];
    }

    // ---- Methods added for Profile-section migration (§15 Step 0) ----

    public async Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default)
    {
        if (emailIds.Count == 0)
            return new HashSet<Guid>();

        var ids = await _dbContext.AccountMergeRequests
            .Where(r => emailIds.Contains(r.PendingEmailId)
                && r.Status == AccountMergeRequestStatus.Pending)
            .Select(r => r.PendingEmailId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    public async Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default) =>
        alternateEmail is null
            ? await _dbContext.AccountMergeRequests.AnyAsync(
                r => r.TargetUserId == targetUserId
                    && EF.Functions.ILike(r.Email, normalizedEmail)
                    && r.Status == AccountMergeRequestStatus.Pending, ct)
            : await _dbContext.AccountMergeRequests.AnyAsync(
                r => r.TargetUserId == targetUserId
                    && (EF.Functions.ILike(r.Email, normalizedEmail) ||
                        EF.Functions.ILike(r.Email, alternateEmail))
                    && r.Status == AccountMergeRequestStatus.Pending, ct);

    public async Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default) =>
        await _dbContext.AccountMergeRequests
            .AnyAsync(r => r.PendingEmailId == pendingEmailId
                && r.Status == AccountMergeRequestStatus.Pending, ct);

    public async Task CreateAsync(AccountMergeRequest request, CancellationToken ct = default)
    {
        _dbContext.AccountMergeRequests.Add(request);
        await _dbContext.SaveChangesAsync(ct);
    }
}
