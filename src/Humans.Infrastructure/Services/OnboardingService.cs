using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Services;

public class OnboardingService : IOnboardingService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly ISystemTeamSync _syncJob;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OnboardingService> _logger;

    public OnboardingService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IEmailService emailService,
        ISystemTeamSync syncJob,
        IMembershipCalculator membershipCalculator,
        IHumansMetrics metrics,
        IClock clock,
        IMemoryCache cache,
        ILogger<OnboardingService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _syncJob = syncJob;
        _membershipCalculator = membershipCalculator;
        _metrics = metrics;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<(List<Profile> Pending, List<Profile> Flagged, HashSet<Guid> PendingAppUserIds)>
        GetReviewQueueAsync(CancellationToken ct = default)
    {
        var reviewableProfiles = await _dbContext.Profiles
            .Include(p => p.User)
            .Where(p => !p.IsApproved && p.RejectedAt == null)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var allUserIds = reviewableProfiles.Select(p => p.UserId).ToList();
        var pendingAppUserIds = await _dbContext.Applications
            .Where(a => allUserIds.Contains(a.UserId) &&
                a.Status == ApplicationStatus.Submitted)
            .Select(a => a.UserId)
            .ToHashSetAsync(ct);

        var flagged = reviewableProfiles
            .Where(p => p.ConsentCheckStatus == ConsentCheckStatus.Flagged)
            .ToList();
        var pending = reviewableProfiles.Except(flagged).ToList();

        return (pending, flagged, pendingAppUserIds);
    }

    public async Task<(Profile? Profile, int ConsentCount, int RequiredConsentCount,
        MemberApplication? PendingApplication)>
        GetReviewDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile == null)
            return (null, 0, 0, null);

        var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, ct);

        var pendingApp = await _dbContext.Applications
            .Where(a => a.UserId == userId && a.Status == ApplicationStatus.Submitted)
            .FirstOrDefaultAsync(ct);

        return (
            profile,
            snapshot.RequiredConsentCount - snapshot.PendingConsentCount,
            snapshot.RequiredConsentCount,
            pendingApp
        );
    }

    public async Task<(List<MemberApplication> Applications, List<(Guid UserId, string DisplayName)> BoardMembers)>
        GetBoardVotingDashboardAsync(CancellationToken ct = default)
    {
        var applications = await _dbContext.Applications
            .Include(a => a.User)
            .Include(a => a.BoardVotes)
            .Where(a => a.Status == ApplicationStatus.Submitted)
            .OrderBy(a => a.MembershipTier)
            .ThenBy(a => a.SubmittedAt)
            .ToListAsync(ct);

        var now = _clock.GetCurrentInstant();
        var boardMemberIds = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                ra.RoleName == RoleNames.Board &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(ct);

        var boardUsers = await _dbContext.Users
            .AsNoTracking()
            .Where(u => boardMemberIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(ct);

        var boardMembers = boardUsers
            .Select(u => (u.Id, u.DisplayName))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (applications, boardMembers);
    }

    public async Task<MemberApplication?> GetBoardVotingDetailAsync(Guid applicationId, CancellationToken ct = default)
    {
        return await _dbContext.Applications
            .Include(a => a.User)
                .ThenInclude(u => u.Profile)
            .Include(a => a.BoardVotes)
                .ThenInclude(v => v.BoardMemberUser)
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct);
    }

    public async Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string reviewerDisplayName, string? notes, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile == null)
            return new OnboardingResult(false, "NotFound");

        if (profile.RejectedAt != null)
            return new OnboardingResult(false, "AlreadyRejected");

        var hasAllRequiredConsents = await _membershipCalculator.HasAllRequiredConsentsAsync(userId, ct);
        if (!hasAllRequiredConsents)
            return new OnboardingResult(false, "ConsentsRequired");

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = ConsentCheckStatus.Cleared;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = reviewerId;
        profile.ConsentCheckNotes = notes;
        profile.IsApproved = true;
        profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.ConsentCheckCleared, nameof(Profile), userId,
            $"Consent check cleared by {reviewerDisplayName}",
            reviewerId, reviewerDisplayName);

        await _dbContext.SaveChangesAsync(ct);
        _cache.Remove(CacheKeys.NavBadgeCounts);

        // Add to profile cache (profile is now approved and not suspended)
        if (_cache.TryGetValue(CacheKeys.ApprovedProfiles, out ConcurrentDictionary<Guid, CachedProfile>? profileCache) && profileCache != null)
        {
            await _dbContext.Entry(profile).Collection(p => p.VolunteerHistory).LoadAsync(ct);
            profileCache[userId] = CachedProfile.Create(profile, profile.User);
        }

        // Sync Volunteers team membership (adds to team + sends welcome email)
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId, CancellationToken.None);

        // If user already has approved tier applications, sync those teams too.
        var approvedTiers = await _dbContext.Applications
            .Where(a => a.UserId == userId && a.Status == ApplicationStatus.Approved)
            .Select(a => a.MembershipTier)
            .Distinct()
            .ToListAsync(ct);

        foreach (var tier in approvedTiers)
        {
            if (tier == MembershipTier.Colaborador)
                await _syncJob.SyncColaboradorsMembershipForUserAsync(userId, CancellationToken.None);
            else if (tier == MembershipTier.Asociado)
                await _syncJob.SyncAsociadosMembershipForUserAsync(userId, CancellationToken.None);
        }

        _logger.LogInformation("Consent check cleared for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string reviewerDisplayName, string? notes, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile == null)
            return new OnboardingResult(false, "NotFound");

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = ConsentCheckStatus.Flagged;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = reviewerId;
        profile.ConsentCheckNotes = notes;
        profile.IsApproved = false;
        profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.ConsentCheckFlagged, nameof(Profile), userId,
            $"Consent check flagged by {reviewerDisplayName}: {notes}",
            reviewerId, reviewerDisplayName);

        await _dbContext.SaveChangesAsync(ct);
        _cache.Remove(CacheKeys.NavBadgeCounts);

        // Remove from profile cache (no longer approved)
        if (_cache.TryGetValue(CacheKeys.ApprovedProfiles, out ConcurrentDictionary<Guid, CachedProfile>? flagCache) && flagCache != null)
        {
            flagCache.TryRemove(userId, out _);
        }

        await DeprovisionApprovalGatedSystemTeamsAsync(userId);

        _logger.LogInformation("Consent check flagged for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<bool> HasBoardVotesAsync(Guid applicationId, CancellationToken ct = default)
    {
        return await _dbContext.BoardVotes.AnyAsync(v => v.ApplicationId == applicationId, ct);
    }

    public async Task<OnboardingResult> CastBoardVoteAsync(
        Guid applicationId, Guid boardMemberUserId, VoteChoice vote, string? note, CancellationToken ct = default)
    {
        var application = await _dbContext.Applications
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct);

        if (application == null)
            return new OnboardingResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new OnboardingResult(false, "NotSubmitted");

        var existingVote = await _dbContext.BoardVotes
            .FirstOrDefaultAsync(v => v.ApplicationId == applicationId && v.BoardMemberUserId == boardMemberUserId, ct);

        var now = _clock.GetCurrentInstant();

        if (existingVote != null)
        {
            existingVote.Vote = vote;
            existingVote.Note = note;
            existingVote.UpdatedAt = now;
        }
        else
        {
            _dbContext.BoardVotes.Add(new BoardVote
            {
                Id = Guid.NewGuid(),
                ApplicationId = applicationId,
                BoardMemberUserId = boardMemberUserId,
                Vote = vote,
                Note = note,
                VotedAt = now
            });
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Board member {UserId} voted {Vote} on application {ApplicationId}",
            boardMemberUserId, vote, applicationId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string reviewerDisplayName, string? reason, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile == null)
            return new OnboardingResult(false, "NotFound");

        if (profile.RejectedAt != null)
            return new OnboardingResult(false, "AlreadyRejected");

        var now = _clock.GetCurrentInstant();

        profile.RejectionReason = reason;
        profile.RejectedAt = now;
        profile.RejectedByUserId = reviewerId;
        profile.IsApproved = false;
        profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.SignupRejected, nameof(Profile), userId,
            $"Signup rejected by {reviewerDisplayName}{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            reviewerId, reviewerDisplayName);

        await _dbContext.SaveChangesAsync(ct);
        _cache.Remove(CacheKeys.NavBadgeCounts);

        // Remove from profile cache (no longer approved)
        if (_cache.TryGetValue(CacheKeys.ApprovedProfiles, out ConcurrentDictionary<Guid, CachedProfile>? rejectCache) && rejectCache != null)
        {
            rejectCache.TryRemove(userId, out _);
        }

        // FIX: Both Admin and OnboardingReview paths now deprovision
        await DeprovisionApprovalGatedSystemTeamsAsync(userId);

        try
        {
            await _emailService.SendSignupRejectedAsync(
                profile.User.Email ?? string.Empty,
                profile.User.DisplayName,
                reason,
                profile.User.PreferredLanguage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send signup rejection email to {UserId}", userId);
        }

        _logger.LogInformation("Signup rejected for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, string adminDisplayName, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Profile == null)
            return new OnboardingResult(false, "NotFound");

        var now = _clock.GetCurrentInstant();

        user.Profile.IsApproved = true;
        user.Profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), userId,
            $"{user.DisplayName} approved as volunteer by {adminDisplayName}",
            adminId, adminDisplayName);

        await _dbContext.SaveChangesAsync(ct);

        // FIX: cache eviction was missing in AdminController
        _cache.Remove(CacheKeys.NavBadgeCounts);

        // Add to profile cache (profile is now approved)
        if (_cache.TryGetValue(CacheKeys.ApprovedProfiles, out ConcurrentDictionary<Guid, CachedProfile>? approveCache) && approveCache != null)
        {
            await _dbContext.Entry(user.Profile).Collection(p => p.VolunteerHistory).LoadAsync(ct);
            approveCache[userId] = CachedProfile.Create(user.Profile, user);
        }

        // Sync Volunteers team membership (adds user if they also have all required consents)
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId);

        _metrics.RecordVolunteerApproved();
        _logger.LogInformation("Admin {AdminId} approved human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string adminDisplayName, string? notes, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Profile == null)
            return new OnboardingResult(false, "NotFound");

        user.Profile.IsSuspended = true;
        user.Profile.AdminNotes = notes;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.MemberSuspended, nameof(User), userId,
            $"{user.DisplayName} suspended by {adminDisplayName}{(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}",
            adminId, adminDisplayName);

        await _dbContext.SaveChangesAsync(ct);

        // Remove from profile cache (suspended)
        if (_cache.TryGetValue(CacheKeys.ApprovedProfiles, out ConcurrentDictionary<Guid, CachedProfile>? suspendCache) && suspendCache != null)
        {
            suspendCache.TryRemove(userId, out _);
        }

        _metrics.RecordMemberSuspended("admin");
        _logger.LogInformation("Admin {AdminId} suspended human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, string adminDisplayName, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Profile == null)
            return new OnboardingResult(false, "NotFound");

        user.Profile.IsSuspended = false;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.MemberUnsuspended, nameof(User), userId,
            $"{user.DisplayName} unsuspended by {adminDisplayName}",
            adminId, adminDisplayName);

        await _dbContext.SaveChangesAsync(ct);

        // Re-add to profile cache if approved
        if (user.Profile.IsApproved)
        {
            if (_cache.TryGetValue(CacheKeys.ApprovedProfiles, out ConcurrentDictionary<Guid, CachedProfile>? unsuspendCache) && unsuspendCache != null)
            {
                await _dbContext.Entry(user.Profile).Collection(p => p.VolunteerHistory).LoadAsync(ct);
                unsuspendCache[userId] = CachedProfile.Create(user.Profile, user);
            }
        }

        _logger.LogInformation("Admin {AdminId} unsuspended human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<bool> SetConsentCheckPendingIfEligibleAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile == null || profile.IsApproved || profile.ConsentCheckStatus != null)
            return false;

        var hasAllConsents = await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (!hasAllConsents)
            return false;

        profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(ct);
        _cache.Remove(CacheKeys.NavBadgeCounts);
        _logger.LogInformation("User {UserId} has all consents signed, consent check set to Pending", userId);

        return true;
    }

    private async Task DeprovisionApprovalGatedSystemTeamsAsync(Guid userId)
    {
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId, CancellationToken.None);
        await _syncJob.SyncColaboradorsMembershipForUserAsync(userId, CancellationToken.None);
        await _syncJob.SyncAsociadosMembershipForUserAsync(userId, CancellationToken.None);
    }

    public async Task<Application.DTOs.AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default)
    {
        var allUserIds = await _dbContext.Users.Select(u => u.Id).ToListAsync(ct);
        var totalMembers = allUserIds.Count;
        var partition = await _membershipCalculator.PartitionUsersAsync(allUserIds, ct);

        var pendingApplications = await _dbContext.Applications
            .CountAsync(a => a.Status == ApplicationStatus.Submitted, ct);

        // Application statistics (non-withdrawn)
        var appStats = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.Status != ApplicationStatus.Withdrawn)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Approved = g.Count(a => a.Status == ApplicationStatus.Approved),
                Rejected = g.Count(a => a.Status == ApplicationStatus.Rejected),
                Colaborador = g.Count(a => a.MembershipTier == MembershipTier.Colaborador),
                Asociado = g.Count(a => a.MembershipTier == MembershipTier.Asociado)
            })
            .FirstOrDefaultAsync(ct);

        return new Application.DTOs.AdminDashboardData(
            totalMembers, partition.IncompleteSignup.Count, partition.PendingApproval.Count,
            partition.Active.Count, partition.MissingConsents.Count,
            partition.Suspended.Count, partition.PendingDeletion.Count, pendingApplications,
            appStats?.Total ?? 0, appStats?.Approved ?? 0, appStats?.Rejected ?? 0,
            appStats?.Colaborador ?? 0, appStats?.Asociado ?? 0);
    }

    public async Task<OnboardingResult> PurgeHumanAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user == null)
            return new OnboardingResult(false, "NotFound");

        var displayName = user.DisplayName;

        // Remove UserEmails so the unique index doesn't block the new account
        var userEmails = await _dbContext.UserEmails.Where(e => e.UserId == userId).ToListAsync(ct);
        _dbContext.UserEmails.RemoveRange(userEmails);

        // Change email so email-based lookup won't match
        var purgedEmail = $"purged-{Guid.NewGuid()}@deleted.local";
        user.Email = purgedEmail;
        user.NormalizedEmail = purgedEmail.ToUpperInvariant();
        user.UserName = purgedEmail;
        user.NormalizedUserName = purgedEmail.ToUpperInvariant();
        user.DisplayName = $"Purged ({displayName})";

        // Lock out the account permanently
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogWarning("Purged human {DisplayName} ({HumanId})", displayName, userId);

        return new OnboardingResult(true);
    }
}
