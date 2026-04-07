using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
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
    private readonly INotificationService _notificationService;
    private readonly INotificationInboxService _notificationInboxService;
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
        INotificationService notificationService,
        INotificationInboxService notificationInboxService,
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
        _notificationService = notificationService;
        _notificationInboxService = notificationInboxService;
        _syncJob = syncJob;
        _membershipCalculator = membershipCalculator;
        _metrics = metrics;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<(List<Profile> Pending, List<Profile> Flagged, HashSet<Guid> PendingAppUserIds,
        Dictionary<Guid, (int Signed, int Required)> ConsentProgress)>
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

        var consentProgress = new Dictionary<Guid, (int Signed, int Required)>();
        foreach (var userId in allUserIds)
        {
            var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, ct);
            consentProgress[userId] = (snapshot.RequiredConsentCount - snapshot.PendingConsentCount, snapshot.RequiredConsentCount);
        }

        var flagged = reviewableProfiles
            .Where(p => p.ConsentCheckStatus == ConsentCheckStatus.Flagged)
            .ToList();
        var pending = reviewableProfiles.Except(flagged).ToList();

        return (pending, flagged, pendingAppUserIds, consentProgress);
    }

    public async Task<(Profile? Profile, int ConsentCount, int RequiredConsentCount,
        MemberApplication? PendingApplication)>
        GetReviewDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
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
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        if (profile.RejectedAt is not null)
            return new OnboardingResult(false, "AlreadyRejected");

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = ConsentCheckStatus.Cleared;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = reviewerId;
        profile.ConsentCheckNotes = notes;
        profile.IsApproved = true;
        profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.ConsentCheckCleared, nameof(Profile), userId,
            $"Consent check cleared",
            reviewerId);

        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();

        // Add to profile cache (profile is now approved and not suspended)
        await _dbContext.Entry(profile).Collection(p => p.VolunteerHistory).LoadAsync(ct);
        _cache.UpdateApprovedProfile(userId, CachedProfile.Create(profile, profile.User));

        // Sync Volunteers team membership (adds to team if consents are also complete)
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
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
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
            $"Consent check flagged: {notes}",
            reviewerId);

        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();

        // Remove from profile cache (no longer approved)
        _cache.UpdateApprovedProfile(userId, null);

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

        if (application is null)
            return new OnboardingResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new OnboardingResult(false, "NotSubmitted");

        var existingVote = await _dbContext.BoardVotes
            .FirstOrDefaultAsync(v => v.ApplicationId == applicationId && v.BoardMemberUserId == boardMemberUserId, ct);

        var now = _clock.GetCurrentInstant();

        if (existingVote is not null)
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

        _cache.InvalidateVotingBadge(boardMemberUserId);

        _logger.LogInformation("Board member {UserId} voted {Vote} on application {ApplicationId}",
            boardMemberUserId, vote, applicationId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        if (profile.RejectedAt is not null)
            return new OnboardingResult(false, "AlreadyRejected");

        var now = _clock.GetCurrentInstant();

        profile.RejectionReason = reason;
        profile.RejectedAt = now;
        profile.RejectedByUserId = reviewerId;
        profile.IsApproved = false;
        profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.SignupRejected, nameof(Profile), userId,
            $"Signup rejected{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            reviewerId);

        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();

        // Remove from profile cache (no longer approved)
        _cache.UpdateApprovedProfile(userId, null);

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

        // In-app notification to the user (best-effort)
        try
        {
            await _notificationService.SendAsync(
                NotificationSource.ProfileRejected,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                "Your signup has been reviewed",
                [userId],
                body: string.IsNullOrWhiteSpace(reason)
                    ? "Your signup could not be approved at this time."
                    : $"Your signup could not be approved: {reason}",
                actionUrl: "/Profile",
                actionLabel: "View profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ProfileRejected notification for user {UserId}", userId);
        }

        _logger.LogInformation("Signup rejected for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = _clock.GetCurrentInstant();

        user.Profile.IsApproved = true;
        user.Profile.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), userId,
            "Approved as volunteer",
            adminId);

        await _dbContext.SaveChangesAsync(ct);

        // FIX: cache eviction was missing in AdminController
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();

        // Add to profile cache (profile is now approved)
        await _dbContext.Entry(user.Profile).Collection(p => p.VolunteerHistory).LoadAsync(ct);
        _cache.UpdateApprovedProfile(userId, CachedProfile.Create(user.Profile, user));

        // Sync Volunteers team membership (adds user if they also have all required consents)
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId);

        _metrics.RecordVolunteerApproved();
        _logger.LogInformation("Admin {AdminId} approved human {HumanId}", adminId, userId);

        // In-app notification to the new volunteer (best-effort)
        try
        {
            await _notificationService.SendAsync(
                NotificationSource.VolunteerApproved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                "Welcome! You have been approved",
                [userId],
                body: "Your profile has been approved. Welcome to the community!",
                actionUrl: "/Profile",
                actionLabel: "View profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch VolunteerApproved notification for user {UserId}", userId);
        }

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Profile is null)
            return new OnboardingResult(false, "NotFound");

        user.Profile.IsSuspended = true;
        user.Profile.AdminNotes = notes;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.MemberSuspended, nameof(User), userId,
            $"Suspended{(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}",
            adminId);

        await _dbContext.SaveChangesAsync(ct);

        // Remove from profile cache (suspended)
        _cache.UpdateApprovedProfile(userId, null);

        // In-app notification to the suspended user (best-effort)
        try
        {
            await _notificationService.SendAsync(
                NotificationSource.AccessSuspended,
                NotificationClass.Actionable,
                NotificationPriority.Critical,
                "Your access has been suspended",
                [userId],
                body: string.IsNullOrWhiteSpace(notes)
                    ? "Your access has been suspended by an administrator."
                    : $"Your access has been suspended: {notes}",
                actionUrl: "/Profile",
                actionLabel: "View profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", userId);
        }

        _metrics.RecordMemberSuspended("admin");
        _logger.LogInformation("Admin {AdminId} suspended human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Profile is null)
            return new OnboardingResult(false, "NotFound");

        user.Profile.IsSuspended = false;
        user.Profile.UpdatedAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.MemberUnsuspended, nameof(User), userId,
            "Unsuspended",
            adminId);

        await _dbContext.SaveChangesAsync(ct);

        // Re-add to profile cache if approved
        if (user.Profile.IsApproved)
        {
            await _dbContext.Entry(user.Profile).Collection(p => p.VolunteerHistory).LoadAsync(ct);
            _cache.UpdateApprovedProfile(userId, CachedProfile.Create(user.Profile, user));
        }

        // Auto-resolve any outstanding AccessSuspended notifications (best-effort)
        try
        {
            await _notificationInboxService.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve AccessSuspended notifications for user {UserId}", userId);
        }

        _logger.LogInformation("Admin {AdminId} unsuspended human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<bool> SetConsentCheckPendingIfEligibleAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null || profile.IsApproved || profile.ConsentCheckStatus is not null)
            return false;

        var hasAllConsents = await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (!hasAllConsents)
            return false;

        profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();
        _logger.LogInformation("User {UserId} has all consents signed, consent check set to Pending", userId);

        // Dispatch in-app notification to Consent Coordinators
        try
        {
            await _notificationService.SendToRoleAsync(
                NotificationSource.ConsentReviewNeeded,
                NotificationClass.Actionable,
                NotificationPriority.High,
                "New consent review needed",
                RoleNames.ConsentCoordinator,
                body: "A human has completed all required consents and needs review.",
                actionUrl: "/OnboardingReview",
                actionLabel: "Review \u2192",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ConsentReviewNeeded notification for user {UserId}", userId);
        }

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
        if (user is null)
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
        _cache.InvalidateActiveTeams();
        _cache.InvalidateApprovedProfiles();

        _logger.LogWarning("Purged human {DisplayName} ({HumanId})", displayName, userId);

        return new OnboardingResult(true);
    }
}
