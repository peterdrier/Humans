using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Mailer;

public sealed class MailerImportService(
    IMailerLiteService ml,
    IUserEmailService userEmails,
    IUserServiceRead users,
    IAccountProvisioningService provisioning,
    ICommunicationPreferenceService prefs,
    IAuditLogService audit,
    IClock clock,
    ILogger<MailerImportService> logger) : IMailerImportService
{
    // The erroneous import pulled the whole MailerLite account; it must only ever
    // ingest the "Website" group. Resolved by name at runtime (group ids aren't
    // stable across environments). Reads of this group bypass the client's
    // "Humans - " write-guard — it's a source, never written to.
    private const string WebsiteGroupName = "Website";

    // UpdateSource the import stamps on Marketing prefs it writes.
    private const string SyncSource = "MailerLiteSync";

    // Source label recorded on the audit entry when a flag is reset to null.
    private const string ResetSource = "MailerLiteSyncReset";

    // Marketing opt-ins written by the erroneous whole-account import carry no
    // genuine prior consent. A Marketing pref whose SubscribedAt predates this
    // instant was opted-in before the bad import and is preserved (≤5 known
    // cases). Hardcoded — one-time GDPR remediation cutoff.
    private static readonly Instant BadImportCutoff = Instant.FromUtc(2026, 5, 19, 0, 1, 1);

    public async Task<ImportPlan> BuildPlanAsync(CancellationToken ct = default)
    {
        var websiteGroupId = await ResolveWebsiteGroupIdAsync(ct);

        var decisions = new List<SubscriberDecision>();
        var subs = new List<MailerLiteSubscriber>();
        await foreach (var s in ml.ListSubscribersAsync(ct))
            if (s.GroupIds.Contains(websiteGroupId, StringComparer.Ordinal))
                subs.Add(s);

        foreach (var s in subs)
        {
            // 1. Unconfirmed
            if (string.Equals(s.Status, "unconfirmed", StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.UnconfirmedSkipped, null, null, null));
                continue;
            }

            // 2. Verified match — count distinct owners so uniqueness drift surfaces as Ambiguous.
            var verifiedUserIds = await userEmails.GetDistinctVerifiedUserIdsAsync(s.Email, ct);
            if (verifiedUserIds.Count > 1)
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.AmbiguousMultipleVerified, null, null, verifiedUserIds));
                continue;
            }
            if (verifiedUserIds.Count == 1)
            {
                var targetId = await ResolveTombstoneAsync(verifiedUserIds[0], ct);
                var existing = await prefs.GetPreferenceOrNullAsync(targetId, MessageCategory.Marketing, ct);
                var outcome = ClassifyVerifiedMatch(s, existing);
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    outcome, targetId, null, null));
                continue;
            }

            // 3. Unverified match
            var row = await userEmails.FindAnyEmailRowByAddressAsync(s.Email, ct);
            if (row is var (uid, emailId))
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.ReplaceUnverifiedEmail, uid, emailId, null));
                continue;
            }

            // 4. No match
            decisions.Add(new SubscriberDecision(s.Email, s.Status,
                SubscriberOutcome.CreateNewHuman, null, null, null));
        }

        // GDPR remediation: revert Marketing opt-ins the erroneous whole-account
        // import set on people who aren't in the Website group at all. Discovered
        // from the Humans side — these users have no Website subscriber row, so they
        // never appear in the subscriber loop above.
        var websiteEmails = WebsiteSubscriberEmails(subs);
        foreach (var u in await users.GetAllUserInfosAsync(ct))
            if (IsResetCandidate(u, websiteEmails))
                decisions.Add(new SubscriberDecision(
                    u.Email ?? "(no verified email)", "n/a",
                    SubscriberOutcome.ResetMarketingFlag, u.Id, null, null));

        return new ImportPlan(decisions, subs.Count);
    }

    /// <summary>
    /// Maps a verified-match subscriber + existing Marketing pref to a bucket.
    /// Re-evaluated at apply time in <see cref="ApplyMarketingDeltaAsync"/>; the
    /// plan bucket is for preview/UI only.
    /// </summary>
    private static SubscriberOutcome ClassifyVerifiedMatch(
        MailerLiteSubscriber ml, CommunicationPreferenceSnapshot? existingMarketing)
    {
        var mlOptedOut = !string.Equals(ml.Status, "active", StringComparison.OrdinalIgnoreCase);
        var mlActionAt = ml.UnsubscribedAt ?? ml.SubscribedAt;

        bool isBounceOrJunk = string.Equals(ml.Status, "bounced", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(ml.Status, "junk", StringComparison.OrdinalIgnoreCase);
        bool isUserAction = existingMarketing is not null
            && (existingMarketing.UpdateSource is "Profile" or "Guest" or "MagicLink" or "OneClick");
        bool humansNewerThanMl = existingMarketing is not null
            && mlActionAt is not null
            && existingMarketing.UpdatedAt > mlActionAt;

        if (!isBounceOrJunk && isUserAction && humansNewerThanMl)
            return SubscriberOutcome.VerifiedKeepHumansPref;

        if (existingMarketing is not null && existingMarketing.OptedOut == mlOptedOut)
            return SubscriberOutcome.VerifiedPrefsAlreadyMatch;

        // No pref row + ML opt-out: Marketing defaults to opted-out (see
        // MessageCategoryExtensions.DefaultOptedOut), so already in sync.
        if (existingMarketing is null && mlOptedOut)
            return SubscriberOutcome.VerifiedPrefsAlreadyMatch;

        return mlOptedOut
            ? SubscriberOutcome.VerifiedFlipToOptOut
            : SubscriberOutcome.VerifiedFlipToOptIn;
    }

    private async Task<Guid> ResolveTombstoneAsync(Guid userId, CancellationToken ct)
    {
        var visited = new HashSet<Guid> { userId };
        var current = userId;
        while (true)
        {
            var user = await users.GetUserInfoAsync(current, ct);
            if (user?.MergedToUserId is not Guid next) return current;
            if (!visited.Add(next)) return current;
            current = next;
        }
    }

    public async Task<ImportResult> ApplyAsync(
        ImportPlan plan, int? maxPerOutcome = null, CancellationToken ct = default)
    {
        var start = clock.GetCurrentInstant();
        int created = 0, flippedIn = 0, flippedOut = 0, preserved = 0,
            replacedUnverified = 0, vanishedBetweenPlanAndApply = 0, marketingReset = 0, errors = 0;
        var pulledIds = new HashSet<string>(StringComparer.Ordinal);

        var websiteGroupId = await ResolveWebsiteGroupIdAsync(ct);

        // Re-pull ML so plan/apply are stateless. Website group only.
        var subsByEmail = new Dictionary<string, MailerLiteSubscriber>(StringComparer.OrdinalIgnoreCase);
        var websiteSubs = new List<MailerLiteSubscriber>();
        await foreach (var s in ml.ListSubscribersAsync(ct))
        {
            if (!s.GroupIds.Contains(websiteGroupId, StringComparer.Ordinal)) continue;
            subsByEmail[s.Email] = s;
            websiteSubs.Add(s);
            pulledIds.Add(s.Id);
        }
        var websiteEmails = WebsiteSubscriberEmails(websiteSubs);

        var (toProcess, throttled) = ApplyThrottle(plan.Decisions, maxPerOutcome);

        foreach (var d in toProcess)
        {
            try
            {
                // Reset candidates aren't ML subscribers — handle before the subsByEmail guard.
                if (d.Outcome == SubscriberOutcome.ResetMarketingFlag)
                {
                    if (await TryResetMarketingFlagAsync(d.TargetUserId!.Value, websiteEmails, ct))
                        marketingReset++;
                    continue;
                }

                if (!subsByEmail.TryGetValue(d.Email, out var subscriber))
                {
                    vanishedBetweenPlanAndApply++;
                    logger.LogWarning("Subscriber {Email} vanished between plan and apply", d.Email);
                    continue;
                }

                switch (d.Outcome)
                {
                    case SubscriberOutcome.UnconfirmedSkipped:
                    case SubscriberOutcome.AmbiguousMultipleVerified:
                        break;

                    case SubscriberOutcome.VerifiedPrefsAlreadyMatch:
                    case SubscriberOutcome.VerifiedFlipToOptIn:
                    case SubscriberOutcome.VerifiedFlipToOptOut:
                    case SubscriberOutcome.VerifiedKeepHumansPref:
                        {
                            var delta = await ApplyMarketingDeltaAsync(d.TargetUserId!.Value, subscriber, ct);
                            if (delta == DeltaResult.FlippedToOptIn) flippedIn++;
                            else if (delta == DeltaResult.FlippedToOptOut) flippedOut++;
                            else if (delta == DeltaResult.Preserved) preserved++;
                            break;
                        }

                    case SubscriberOutcome.ReplaceUnverifiedEmail:
                        {
                            if (d.UnverifiedEmailIdToDelete is Guid emailId && d.TargetUserId is Guid uid)
                                await userEmails.DeleteEmailAsync(uid, emailId, ct);
                            var (provUser, provCreated) = await provisioning.FindOrCreateUserByEmailAsync(
                                subscriber.Email, displayName: null, ContactSource.MailerLite, ct);
                            if (provCreated) created++;
                            var delta = await ApplyMarketingDeltaAsync(provUser.Id, subscriber, ct);
                            if (delta == DeltaResult.FlippedToOptIn) flippedIn++;
                            else if (delta == DeltaResult.FlippedToOptOut) flippedOut++;
                            replacedUnverified++;
                            break;
                        }

                    case SubscriberOutcome.CreateNewHuman:
                        {
                            var (provUser, provCreated) = await provisioning.FindOrCreateUserByEmailAsync(
                                subscriber.Email, displayName: null, ContactSource.MailerLite, ct);
                            if (provCreated) created++;
                            var delta = await ApplyMarketingDeltaAsync(provUser.Id, subscriber, ct);
                            if (delta == DeltaResult.FlippedToOptIn) flippedIn++;
                            else if (delta == DeltaResult.FlippedToOptOut) flippedOut++;
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                logger.LogError(ex, "Mailer apply failed for {Email}", d.Email);
            }
        }

        var elapsed = clock.GetCurrentInstant() - start;
        var result = new ImportResult(
            TotalPulled: pulledIds.Count,
            HumansCreated: created,
            PrefsFlippedToOptIn: flippedIn,
            PrefsFlippedToOptOut: flippedOut,
            PrefsKeptByConflict: preserved,
            MarketingFlagsReset: marketingReset,
            UnverifiedEmailsReplaced: replacedUnverified,
            AmbiguousSkipped: plan.Counts.AmbiguousMultipleVerified,
            UnconfirmedSkipped: plan.Counts.UnconfirmedSkipped,
            VanishedBetweenPlanAndApply: vanishedBetweenPlanAndApply,
            DecisionsThrottled: throttled,
            Errors: errors,
            Elapsed: elapsed);

        await audit.LogAsync(
            AuditAction.MailerLiteReconciliationCompleted,
            entityType: "Mailer", entityId: Guid.Empty,
            description: result.FormatSummary(),
            jobName: nameof(MailerImportService));

        return result;
    }

    /// <summary>
    /// Splits decisions into the first <paramref name="maxPerOutcome"/> per outcome bucket
    /// (preserving input order) and the throttled count. Null/non-positive processes all.
    /// </summary>
    private static (IReadOnlyList<SubscriberDecision> ToProcess, int Throttled) ApplyThrottle(
        IReadOnlyList<SubscriberDecision> decisions, int? maxPerOutcome)
    {
        if (maxPerOutcome is not int limit || limit <= 0)
            return (decisions, 0);

        var counts = new Dictionary<SubscriberOutcome, int>();
        var toProcess = new List<SubscriberDecision>(decisions.Count);
        int throttled = 0;
        foreach (var d in decisions)
        {
            // No-write outcomes bypass the throttle to avoid double-counting against plan.Counts.
            if (d.Outcome is SubscriberOutcome.UnconfirmedSkipped
                          or SubscriberOutcome.AmbiguousMultipleVerified
                          or SubscriberOutcome.VerifiedPrefsAlreadyMatch)
            {
                toProcess.Add(d);
                continue;
            }

            counts.TryGetValue(d.Outcome, out var taken);
            if (taken < limit)
            {
                counts[d.Outcome] = taken + 1;
                toProcess.Add(d);
            }
            else
            {
                throttled++;
            }
        }
        return (toProcess, throttled);
    }

    private enum DeltaResult { NoChange, FlippedToOptIn, FlippedToOptOut, Preserved }

    private async Task<DeltaResult> ApplyMarketingDeltaAsync(
        Guid userId, MailerLiteSubscriber ml, CancellationToken ct)
    {
        var mlOptedOut = !string.Equals(ml.Status, "active", StringComparison.OrdinalIgnoreCase);
        var marketing = await prefs.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, ct);

        var outcome = ClassifyVerifiedMatch(ml, marketing);
        switch (outcome)
        {
            case SubscriberOutcome.VerifiedKeepHumansPref:
                return DeltaResult.Preserved;
            case SubscriberOutcome.VerifiedPrefsAlreadyMatch:
                return DeltaResult.NoChange;
        }

        await prefs.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: mlOptedOut, source: SyncSource, ct);
        return mlOptedOut ? DeltaResult.FlippedToOptOut : DeltaResult.FlippedToOptIn;
    }

    private async Task<string> ResolveWebsiteGroupIdAsync(CancellationToken ct)
    {
        var groups = await ml.ListGroupsAsync(ct);
        var website = groups.FirstOrDefault(g =>
            string.Equals(g.Name, WebsiteGroupName, StringComparison.OrdinalIgnoreCase));
        return website?.Id
            ?? throw new InvalidOperationException(
                $"MailerLite group '{WebsiteGroupName}' not found — refusing to import or reset " +
                "(would otherwise sweep the whole account / reset every opted-in human).");
    }

    private static HashSet<string> WebsiteSubscriberEmails(IEnumerable<MailerLiteSubscriber> websiteSubs) =>
        websiteSubs
            .Select(s => s.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A user is a reset candidate when their Marketing pref was opted-in by the
    /// erroneous import (<see cref="SyncSource"/>), they have no genuine prior
    /// consent (a <c>SubscribedAt</c> predating <see cref="BadImportCutoff"/>),
    /// and they are not a member of the Website group at all (by any of their
    /// emails). Website members — active or not — are owned by the import loop
    /// (active → opt-in, unsubscribed/bounced → opt-out), so opt-ins survive only
    /// for active members and an explicit unsubscribe stays opt-out rather than
    /// being nulled here. Non-members are pure collateral: the row is deleted to
    /// revert to "no preference" (null).
    /// </summary>
    private static bool IsResetCandidate(UserInfo user, HashSet<string> websiteEmails)
    {
        var marketing = user.CommunicationPreferences
            .FirstOrDefault(c => c.Category == MessageCategory.Marketing);
        if (marketing is null || marketing.OptedOut) return false;
        if (!string.Equals(marketing.UpdateSource, SyncSource, StringComparison.Ordinal)) return false;
        if (marketing.SubscribedAt is { } subscribedAt && subscribedAt < BadImportCutoff) return false;
        return !user.UserEmails.Any(e => websiteEmails.Contains(e.Email));
    }

    // Re-checks the predicate against current cache state so a pref the user set
    // between preview and commit isn't clobbered, then deletes the row (→ null).
    private async Task<bool> TryResetMarketingFlagAsync(
        Guid userId, HashSet<string> websiteEmails, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null || !IsResetCandidate(info, websiteEmails))
            return false;
        await prefs.ResetPreferenceAsync(userId, MessageCategory.Marketing, ResetSource, ct);
        return true;
    }
}
