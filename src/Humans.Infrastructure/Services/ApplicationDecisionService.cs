using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;

namespace Humans.Infrastructure.Services;

public class ApplicationDecisionService : IApplicationDecisionService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly SystemTeamSyncJob _syncJob;
    private readonly HumansMetricsService _metrics;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ApplicationDecisionService> _logger;

    public ApplicationDecisionService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IEmailService emailService,
        SystemTeamSyncJob syncJob,
        HumansMetricsService metrics,
        IClock clock,
        IMemoryCache cache,
        ILogger<ApplicationDecisionService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _syncJob = syncJob;
        _metrics = metrics;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reviewerDisplayName,
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

        if (application == null)
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
        if (profile != null)
        {
            profile.MembershipTier = application.MembershipTier;
            profile.UpdatedAt = _clock.GetCurrentInstant();
        }

        // Audit
        await _auditLogService.LogAsync(
            AuditAction.TierApplicationApproved, "Application", application.Id,
            $"{application.MembershipTier} application approved for {application.User.DisplayName} by {reviewerDisplayName}",
            reviewerUserId, reviewerDisplayName);

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

        _cache.Remove(CacheKeys.NavBadgeCounts);
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

        return new ApplicationDecisionResult(true);
    }

    public async Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reviewerDisplayName,
        string reason,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var application = await _dbContext.Applications
            .Include(a => a.User)
            .Include(a => a.BoardVotes)
            .Include(a => a.StateHistory)
            .FirstOrDefaultAsync(a => a.Id == applicationId, cancellationToken);

        if (application == null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        // State transition
        application.Reject(reviewerUserId, reason, _clock);
        application.BoardMeetingDate = boardMeetingDate;
        application.DecisionNote = reason;

        // Audit
        await _auditLogService.LogAsync(
            AuditAction.TierApplicationRejected, "Application", application.Id,
            $"{application.MembershipTier} application rejected for {application.User.DisplayName} by {reviewerDisplayName}",
            reviewerUserId, reviewerDisplayName);

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

        _cache.Remove(CacheKeys.NavBadgeCounts);
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

        return new ApplicationDecisionResult(true);
    }
}
