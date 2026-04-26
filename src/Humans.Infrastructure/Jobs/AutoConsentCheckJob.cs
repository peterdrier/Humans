using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: for every human sitting in the Consent Check = Pending bucket,
/// ask the LLM whether their legal name (a) looks like a plausible real name and
/// (b) matches the admin-maintained hold list. If both checks pass, auto-clear the
/// consent check on their behalf. Otherwise, leave the entry in the manual review
/// queue untouched — a human Consent Coordinator will look at it.
///
/// Only ever APPROVES. Never flags, never rejects. Failures (LLM timeout, API
/// error, malformed response) leave the entry in the queue.
///
/// Kill switch: set <see cref="SyncServiceType.AutoConsentCheck"/> to
/// <see cref="SyncMode.None"/> at /Google/SyncSettings (the shared admin
/// SyncSettings page) to disable without redeploying.
/// </summary>
public class AutoConsentCheckJob : IRecurringJob
{
    private readonly IOnboardingService _onboardingService;
    private readonly IConsentHoldListService _holdListService;
    private readonly IConsentCheckAssistant _assistant;
    private readonly IProfileService _profileService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISyncSettingsService _syncSettingsService;
    private readonly HumansDbContext _dbContext;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly ILogger<AutoConsentCheckJob> _logger;

    public AutoConsentCheckJob(
        IOnboardingService onboardingService,
        IConsentHoldListService holdListService,
        IConsentCheckAssistant assistant,
        IProfileService profileService,
        IAuditLogService auditLogService,
        ISyncSettingsService syncSettingsService,
        HumansDbContext dbContext,
        IHumansMetrics metrics,
        IClock clock,
        ILogger<AutoConsentCheckJob> logger)
    {
        _onboardingService = onboardingService;
        _holdListService = holdListService;
        _assistant = assistant;
        _profileService = profileService;
        _auditLogService = auditLogService;
        _syncSettingsService = syncSettingsService;
        _dbContext = dbContext;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.AutoConsentCheck, cancellationToken);
        if (mode == SyncMode.None)
        {
            _logger.LogInformation(
                "AutoConsentCheckJob skipped: SyncServiceType.AutoConsentCheck is set to None");
            _metrics.RecordJobRun("auto_consent_check", "skipped");
            return;
        }

        _logger.LogInformation("Starting AutoConsentCheckJob at {Time}", _clock.GetCurrentInstant());

        IReadOnlyList<Guid> pendingUserIds;
        try
        {
            pendingUserIds = await _onboardingService.GetPendingConsentCheckUserIdsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("auto_consent_check", "failure");
            _logger.LogError(ex, "AutoConsentCheckJob: failed to load pending consent-check queue");
            throw;
        }

        if (pendingUserIds.Count == 0)
        {
            _logger.LogInformation("AutoConsentCheckJob: no pending consent-check entries to evaluate");
            _metrics.RecordJobRun("auto_consent_check", "success");
            return;
        }

        IReadOnlyList<ConsentHoldListEntry> holdListEntries;
        try
        {
            holdListEntries = await _holdListService.ListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("auto_consent_check", "failure");
            _logger.LogError(ex, "AutoConsentCheckJob: failed to load hold list");
            throw;
        }

        var holdList = holdListEntries
            .Select(e => e.Entry)
            .ToList();

        var profilesByUserId = await _profileService.GetByUserIdsAsync(pendingUserIds, cancellationToken);

        var approvedCount = 0;
        var skippedCount = 0;

        foreach (var userId in pendingUserIds)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (!profilesByUserId.TryGetValue(userId, out var profile) || profile is null)
            {
                _logger.LogWarning(
                    "AutoConsentCheckJob: profile not found for user {UserId} (skipped)", userId);
                skippedCount++;
                continue;
            }

            var legalName = $"{profile.FirstName} {profile.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(legalName))
            {
                _logger.LogInformation(
                    "AutoConsentCheckJob: skipping user {UserId} — legal name is empty", userId);
                await SafeLogSkipAsync(userId, "(no legal name on profile)", modelId: "n/a", cancellationToken);
                skippedCount++;
                continue;
            }

            ConsentCheckVerdict verdict;
            try
            {
                verdict = await _assistant.EvaluateAsync(legalName, holdList, cancellationToken);
            }
            catch (Exception ex)
            {
                // Any LLM failure leaves the entry untouched — human reviewer handles it.
                _logger.LogWarning(
                    ex, "AutoConsentCheckJob: LLM evaluation failed for user {UserId}; leaving in manual queue",
                    userId);
                skippedCount++;
                continue;
            }

            if (verdict.PlausibleRealName && !verdict.HoldListMatch)
            {
                var result = await _onboardingService.AutoClearConsentCheckAsync(
                    userId, verdict.Reason, verdict.ModelId, cancellationToken);
                if (result.Success)
                {
                    _logger.LogInformation(
                        "AutoConsentCheckJob: auto-cleared user {UserId} (model {ModelId}): {Reason}",
                        userId, verdict.ModelId, verdict.Reason);
                    approvedCount++;
                }
                else
                {
                    _logger.LogWarning(
                        "AutoConsentCheckJob: AutoClearConsentCheckAsync for user {UserId} returned {ErrorKey}",
                        userId, result.ErrorKey);
                    skippedCount++;
                }
            }
            else
            {
                var reasonLabel = BuildSkipReason(verdict);
                _logger.LogInformation(
                    "AutoConsentCheckJob: skipping user {UserId} ({Reason}, model {ModelId})",
                    userId, reasonLabel, verdict.ModelId);
                await SafeLogSkipAsync(userId, reasonLabel, verdict.ModelId, cancellationToken);
                skippedCount++;
            }
        }

        _metrics.RecordJobRun("auto_consent_check", "success");
        _logger.LogInformation(
            "AutoConsentCheckJob finished: approved={Approved}, skipped={Skipped}, total={Total}",
            approvedCount, skippedCount, pendingUserIds.Count);
    }

    private async Task SafeLogSkipAsync(Guid userId, string reason, string modelId, CancellationToken ct)
    {
        try
        {
            // IAuditLogService.LogAsync queues to the DbContext but does not save —
            // skips don't mutate Profile state, so we save audit-only writes here.
            await _auditLogService.LogAsync(
                AuditAction.ConsentCheckAutoSkipped,
                nameof(Profile),
                userId,
                $"Auto consent-check skipped by {modelId}: {reason}",
                nameof(AutoConsentCheckJob));
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AutoConsentCheckJob: failed to write skip-audit entry for user {UserId}", userId);
        }
    }

    private static string BuildSkipReason(ConsentCheckVerdict verdict)
    {
        var parts = new List<string>(2);
        if (!verdict.PlausibleRealName) parts.Add("name not plausible");
        if (verdict.HoldListMatch) parts.Add("hold-list match");
        var label = parts.Count == 0 ? "no-op" : string.Join(" + ", parts);
        return string.IsNullOrWhiteSpace(verdict.Reason) ? label : $"{label}: {verdict.Reason}";
    }
}
