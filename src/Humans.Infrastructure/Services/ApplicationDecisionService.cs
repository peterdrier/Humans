using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Services;

public class ApplicationDecisionService : IApplicationDecisionService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly ISystemTeamSync _syncJob;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ApplicationDecisionService> _logger;

    public ApplicationDecisionService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IEmailService emailService,
        INotificationService notificationService,
        ISystemTeamSync syncJob,
        IHumansMetrics metrics,
        IClock clock,
        IMemoryCache cache,
        ILogger<ApplicationDecisionService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _notificationService = notificationService;
        _syncJob = syncJob;
        _metrics = metrics;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var application = await _dbContext.Applications
            .Include(a => a.User)
                .ThenInclude(u => u.Profile)
            .Include(a => a.BoardVotes)
            .Include(a => a.StateHistory)
            .FirstOrDefaultAsync(a => a.Id == applicationId, cancellationToken);

        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        // State transition
        application.Approve(reviewerUserId, notes, _clock);
        application.BoardMeetingDate = boardMeetingDate;
        application.DecisionNote = notes;

        // Term expiry
        var today = _clock.GetCurrentInstant().InUtc().Date;
        application.TermExpiresAt = TermExpiryCalculator.ComputeTermExpiry(today);

        // Update profile membership tier
        var profile = application.User.Profile;
        if (profile is not null)
        {
            profile.MembershipTier = application.MembershipTier;
            profile.UpdatedAt = _clock.GetCurrentInstant();
        }

        // Audit
        await _auditLogService.LogAsync(
            AuditAction.TierApplicationApproved, nameof(Humans.Domain.Entities.Application), application.Id,
            $"{application.MembershipTier} application approved",
            reviewerUserId);

        // Capture voter IDs before GDPR deletion clears the navigation collection
        var voterIds = application.BoardVotes.Select(v => v.BoardMemberUserId).ToList();

        // GDPR: delete individual board votes
        _dbContext.BoardVotes.RemoveRange(application.BoardVotes);

        // Save (must complete before team sync)
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                _logger.LogWarning(
                    "Concurrency conflict on entity {EntityType} (State={State}). " +
                    "Original values: {Original}, Current values: {Current}",
                    entry.Metadata.Name, entry.State,
                    string.Join(", ", entry.Properties
                        .Where(p => p.IsModified || p.Metadata.IsConcurrencyToken)
                        .Select(p => $"{p.Metadata.Name}={p.OriginalValue}→{p.CurrentValue}")),
                    string.Join(", ", entry.Properties
                        .Where(p => p.Metadata.IsConcurrencyToken)
                        .Select(p => $"{p.Metadata.Name}: db={p.OriginalValue}")));
            }
            _logger.LogWarning(ex,
                "Concurrency conflict while approving application {ApplicationId} by {UserId}",
                application.Id, reviewerUserId);
            return new ApplicationDecisionResult(false, "ConcurrencyConflict");
        }

        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();
        foreach (var id in voterIds)
            _cache.InvalidateVotingBadge(id);
        _metrics.RecordApplicationProcessed("approved");
        _logger.LogInformation("Application {ApplicationId} approved by {UserId}",
            application.Id, reviewerUserId);

        // Sync team membership
        if (application.MembershipTier == MembershipTier.Colaborador)
            await _syncJob.SyncColaboradorsMembershipForUserAsync(application.UserId, cancellationToken);
        else if (application.MembershipTier == MembershipTier.Asociado)
            await _syncJob.SyncAsociadosMembershipForUserAsync(application.UserId, cancellationToken);

        // Notification email (best-effort)
        try
        {
            await _emailService.SendApplicationApprovedAsync(
                application.User.Email ?? string.Empty,
                application.User.DisplayName,
                application.MembershipTier,
                application.User.PreferredLanguage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send approval email for {ApplicationId}", application.Id);
        }

        // In-app notification to applicant (best-effort)
        try
        {
            await _notificationService.SendAsync(
                NotificationSource.ApplicationApproved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Your {application.MembershipTier} application has been approved",
                [application.UserId],
                body: $"Congratulations! Your {application.MembershipTier} application has been approved.",
                actionUrl: "/Governance/MyApplications",
                actionLabel: "View application",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ApplicationApproved notification for {ApplicationId}",
                application.Id);
        }

        return new ApplicationDecisionResult(true);
    }

    public async Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reason,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var application = await _dbContext.Applications
            .Include(a => a.User)
            .Include(a => a.BoardVotes)
            .Include(a => a.StateHistory)
            .FirstOrDefaultAsync(a => a.Id == applicationId, cancellationToken);

        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        // State transition
        application.Reject(reviewerUserId, reason, _clock);
        application.BoardMeetingDate = boardMeetingDate;
        application.DecisionNote = reason;

        // Audit
        await _auditLogService.LogAsync(
            AuditAction.TierApplicationRejected, nameof(Humans.Domain.Entities.Application), application.Id,
            $"{application.MembershipTier} application rejected",
            reviewerUserId);

        // Capture voter IDs before GDPR deletion clears the navigation collection
        var voterIds = application.BoardVotes.Select(v => v.BoardMemberUserId).ToList();

        // GDPR: delete individual board votes
        _dbContext.BoardVotes.RemoveRange(application.BoardVotes);

        // Save
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                _logger.LogWarning(
                    "Concurrency conflict on entity {EntityType} (State={State}). " +
                    "Modified/token props: {Props}",
                    entry.Metadata.Name, entry.State,
                    string.Join(", ", entry.Properties
                        .Where(p => p.IsModified || p.Metadata.IsConcurrencyToken)
                        .Select(p => $"{p.Metadata.Name}={p.OriginalValue}→{p.CurrentValue}")));
            }
            _logger.LogWarning(ex,
                "Concurrency conflict while rejecting application {ApplicationId} by {UserId}",
                application.Id, reviewerUserId);
            return new ApplicationDecisionResult(false, "ConcurrencyConflict");
        }

        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();
        foreach (var id in voterIds)
            _cache.InvalidateVotingBadge(id);
        _metrics.RecordApplicationProcessed("rejected");
        _logger.LogInformation("Application {ApplicationId} rejected by {UserId}",
            application.Id, reviewerUserId);

        // Notification email (best-effort)
        try
        {
            await _emailService.SendApplicationRejectedAsync(
                application.User.Email ?? string.Empty,
                application.User.DisplayName,
                application.MembershipTier,
                reason,
                application.User.PreferredLanguage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send rejection email for {ApplicationId}", application.Id);
        }

        // In-app notification to applicant (best-effort)
        try
        {
            await _notificationService.SendAsync(
                NotificationSource.ApplicationRejected,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Your {application.MembershipTier} application was not approved",
                [application.UserId],
                body: $"Your {application.MembershipTier} application was not approved.",
                actionUrl: "/Governance/MyApplications",
                actionLabel: "View application",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ApplicationRejected notification for {ApplicationId}",
                application.Id);
        }

        return new ApplicationDecisionResult(true);
    }

    public async Task<IReadOnlyList<MemberApplication>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.Applications
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);
    }

    public async Task<MemberApplication?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.Applications
            .Include(a => a.ReviewedByUser)
            .Include(a => a.StateHistory)
                .ThenInclude(h => h.ChangedByUser)
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.UserId == userId, ct);
    }

    public async Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default)
    {
        // Check for existing pending application
        var hasPending = await _dbContext.Applications
            .AnyAsync(a => a.UserId == userId && a.Status == ApplicationStatus.Submitted, ct);

        if (hasPending)
            return new ApplicationDecisionResult(false, "AlreadyPending");

        var now = _clock.GetCurrentInstant();

        var application = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = tier,
            Motivation = motivation,
            AdditionalInfo = additionalInfo,
            SignificantContribution = tier == MembershipTier.Asociado ? significantContribution : null,
            RoleUnderstanding = tier == MembershipTier.Asociado ? roleUnderstanding : null,
            Language = language,
            SubmittedAt = now,
            UpdatedAt = now
        };

        _dbContext.Applications.Add(application);
        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();

        _logger.LogInformation("User {UserId} submitted application {ApplicationId}", userId, application.Id);

        // Dispatch in-app notification to Board members
        try
        {
            await _notificationService.SendToRoleAsync(
                NotificationSource.ApplicationSubmitted,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                $"New {tier} application submitted",
                RoleNames.Board,
                body: $"A new {tier} application requires Board review.",
                actionUrl: "/OnboardingReview/BoardVoting",
                actionLabel: "Review \u2192",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ApplicationSubmitted notification for application {ApplicationId}",
                application.Id);
        }

        return new ApplicationDecisionResult(true, ApplicationId: application.Id);
    }

    public async Task<ApplicationDecisionResult> WithdrawAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var application = await _dbContext.Applications
            .Include(a => a.StateHistory)
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.UserId == userId, ct);

        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "CannotWithdraw");

        application.Withdraw(_clock);
        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();
        _metrics.RecordApplicationProcessed("withdrawn");

        _logger.LogInformation("User {UserId} withdrew application {ApplicationId}", userId, applicationId);

        return new ApplicationDecisionResult(true);
    }

    public async Task<(IReadOnlyList<MemberApplication> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _dbContext.Applications
            .AsNoTracking()
            .Include(a => a.User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(statusFilter) && Enum.TryParse<ApplicationStatus>(statusFilter, ignoreCase: true, out var statusEnum))
        {
            query = query.Where(a => a.Status == statusEnum);
        }
        else
        {
            // Default: show pending applications
            query = query.Where(a => a.Status == ApplicationStatus.Submitted);
        }

        if (!string.IsNullOrWhiteSpace(tierFilter) && Enum.TryParse<MembershipTier>(tierFilter, ignoreCase: true, out var tierEnum))
        {
            query = query.Where(a => a.MembershipTier == tierEnum);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderBy(a => a.SubmittedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<MemberApplication?> GetApplicationDetailAsync(Guid applicationId, CancellationToken ct = default)
    {
        return await _dbContext.Applications
            .Include(a => a.User)
            .Include(a => a.ReviewedByUser)
            .Include(a => a.StateHistory)
                .ThenInclude(h => h.ChangedByUser)
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct);
    }
}
