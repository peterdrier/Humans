using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Mailer;

public sealed class MailerImportService : IMailerImportService
{
    private readonly IMailerLiteService _ml;
    private readonly IUserEmailService _userEmails;
    private readonly IUserService _users;
    private readonly IAccountProvisioningService _provisioning;
    private readonly ICommunicationPreferenceService _prefs;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly ILogger<MailerImportService> _logger;

    public MailerImportService(
        IMailerLiteService ml,
        IUserEmailService userEmails,
        IUserService users,
        IAccountProvisioningService provisioning,
        ICommunicationPreferenceService prefs,
        IAuditLogService audit,
        IClock clock,
        ILogger<MailerImportService> logger)
    {
        _ml = ml;
        _userEmails = userEmails;
        _users = users;
        _provisioning = provisioning;
        _prefs = prefs;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ImportPlan> BuildPlanAsync(CancellationToken ct = default)
    {
        var decisions = new List<SubscriberDecision>();
        var subs = new List<MailerLiteSubscriber>();
        await foreach (var s in _ml.ListSubscribersAsync(ct)) subs.Add(s);

        foreach (var s in subs)
        {
            // 1. Unconfirmed
            if (string.Equals(s.Status, "unconfirmed", StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.UnconfirmedSkipped, null, null, null));
                continue;
            }

            // 2. Verified match — count distinct owners so service-level
            // uniqueness drift (multiple users sharing the same verified
            // address) surfaces as AmbiguousMultipleVerified instead of
            // silently mutating one arbitrary user's preferences.
            var verifiedUserIds = await _userEmails.GetDistinctVerifiedUserIdsAsync(s.Email, ct);
            if (verifiedUserIds.Count > 1)
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.AmbiguousMultipleVerified, null, null, verifiedUserIds));
                continue;
            }
            if (verifiedUserIds.Count == 1)
            {
                var targetId = await ResolveTombstoneAsync(verifiedUserIds[0], ct);
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.AttachVerified, targetId, null, null));
                continue;
            }

            // 3. Unverified match
            var row = await _userEmails.FindAnyEmailRowByAddressAsync(s.Email, ct);
            if (row is var (uid, emailId))
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.DeleteUnverifiedThenCreate, uid, emailId, null));
                continue;
            }

            // 4. No match
            decisions.Add(new SubscriberDecision(s.Email, s.Status,
                SubscriberOutcome.CreateContact, null, null, null));
        }

        return new ImportPlan(decisions, subs.Count);
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

    public async Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken ct = default)
    {
        var start = _clock.GetCurrentInstant();
        int created = 0, flipped = 0, preserved = 0, deletedAndCreated = 0, vanishedBetweenPlanAndApply = 0, errors = 0;
        var pulledIds = new HashSet<string>(StringComparer.Ordinal);

        // Re-pull ML so plan/apply are stateless.
        var subsByEmail = new Dictionary<string, MailerLiteSubscriber>(StringComparer.OrdinalIgnoreCase);
        await foreach (var s in _ml.ListSubscribersAsync(ct))
        {
            subsByEmail[s.Email] = s;
            pulledIds.Add(s.Id);
        }

        foreach (var d in plan.Decisions)
        {
            try
            {
                if (!subsByEmail.TryGetValue(d.Email, out var subscriber))
                {
                    vanishedBetweenPlanAndApply++;
                    _logger.LogWarning("Subscriber {Email} vanished between plan and apply", d.Email);
                    continue;
                }

                switch (d.Outcome)
                {
                    case SubscriberOutcome.UnconfirmedSkipped:
                    case SubscriberOutcome.AmbiguousMultipleVerified:
                    case SubscriberOutcome.AttachVerifiedConfirmOnly:
                        break;

                    case SubscriberOutcome.AttachVerified:
                        {
                            var delta = await ApplyMarketingDeltaAsync(d.TargetUserId!.Value, subscriber, ct);
                            if (delta == DeltaResult.Flipped) flipped++;
                            else if (delta == DeltaResult.Preserved) preserved++;
                            break;
                        }

                    case SubscriberOutcome.DeleteUnverifiedThenCreate:
                        {
                            if (d.UnverifiedEmailIdToDelete is Guid emailId && d.TargetUserId is Guid uid)
                                await _userEmails.DeleteEmailAsync(uid, emailId, ct);
                            var (provUser, provCreated) = await _provisioning.FindOrCreateUserByEmailAsync(
                                subscriber.Email, displayName: null, ContactSource.MailerLite, ct);
                            if (provCreated) created++;
                            await ApplyMarketingDeltaAsync(provUser.Id, subscriber, ct);
                            deletedAndCreated++;
                            break;
                        }

                    case SubscriberOutcome.CreateContact:
                        {
                            var (provUser, provCreated) = await _provisioning.FindOrCreateUserByEmailAsync(
                                subscriber.Email, displayName: null, ContactSource.MailerLite, ct);
                            if (provCreated) created++;
                            await ApplyMarketingDeltaAsync(provUser.Id, subscriber, ct);
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                _logger.LogError(ex, "Mailer apply failed for {Email}", d.Email);
            }
        }

        var elapsed = _clock.GetCurrentInstant() - start;
        var result = new ImportResult(
            TotalPulled: pulledIds.Count,
            ContactsCreated: created,
            PrefsFlipped: flipped,
            PrefsPreservedByConflict: preserved,
            UnverifiedRowsDeletedAndSuperseded: deletedAndCreated,
            AmbiguousSkipped: plan.Counts.SkippedAmbiguous,
            UnconfirmedSkipped: plan.Counts.SkippedUnconfirmed,
            VanishedBetweenPlanAndApply: vanishedBetweenPlanAndApply,
            Errors: errors,
            Elapsed: elapsed);

        await _audit.LogAsync(
            AuditAction.MailerLiteReconciliationCompleted,
            entityType: "Mailer", entityId: Guid.Empty,
            description: result.FormatSummary(),
            jobName: nameof(MailerImportService));

        return result;
    }

    private enum DeltaResult { NoChange, Flipped, Preserved }

    private async Task<DeltaResult> ApplyMarketingDeltaAsync(
        Guid userId, MailerLiteSubscriber ml, CancellationToken ct)
    {
        var mlOptedOut = !string.Equals(ml.Status, "active", StringComparison.OrdinalIgnoreCase);
        var mlActionAt = ml.UnsubscribedAt ?? ml.SubscribedAt;

        var prefs = await _prefs.GetPreferencesAsync(userId, ct);
        var marketing = prefs.FirstOrDefault(p => p.Category == MessageCategory.Marketing);

        bool isBounceOrJunk = string.Equals(ml.Status, "bounced", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(ml.Status, "junk", StringComparison.OrdinalIgnoreCase);
        bool isUserAction = marketing is not null
            && (marketing.UpdateSource is "Profile" or "Guest" or "MagicLink" or "OneClick");
        bool humansNewerThanMl = marketing is not null
            && mlActionAt is not null
            && marketing.UpdatedAt > mlActionAt;

        if (!isBounceOrJunk && isUserAction && humansNewerThanMl)
            return DeltaResult.Preserved;

        if (marketing is not null && marketing.OptedOut == mlOptedOut)
            return DeltaResult.NoChange;

        await _prefs.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: mlOptedOut, source: "MailerLiteSync", ct);
        return DeltaResult.Flipped;
    }
}
