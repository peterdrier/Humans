using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteEffects(
    IAssemblyVoteRepository repository,
    IUserService users,
    IEmailService email,
    GovernanceEmails messages,
    INotificationEmitter notifications,
    INotificationAutoResolve notificationResolve,
    IAuditLogService audit,
    IClock clock,
    ILogger logger,
    int maxTitleLength,
    string lapseJobName) : IApplicationService
{
    /// <summary>How far ahead of closing the reminder goes out.</summary>
    private static readonly Duration ReminderLeadTime = Duration.FromHours(24);

    /// <summary>
    /// Best-effort: every caller has already persisted a terminal status with no retry, so a throw
    /// here would strand the audit and email work still owed. Logged and swallowed.
    /// </summary>
    internal Task ClearOpenNotificationAsync(
        AssemblyVote vote, Guid? actorUserId, CancellationToken _) =>
        AfterTransitionAsync(
            "resolving the open-vote notification", vote.Id,
            ct => notificationResolve.ResolveBySourceKeyAsync(
                GovernanceNotificationSources.AssemblyVoteOpened, vote.Id.ToString(), actorUserId, ct));

    /// <summary>
    /// Runs a side effect after an irreversible, committed transition without letting a throw
    /// leave the audit entry, roster email or notification permanently missing.
    /// </summary>
    internal async Task AfterTransitionAsync(
        string what, Guid voteId, Func<CancellationToken, Task> work)
    {
        try
        {
            // Deliberately not the request's token: the change is committed, and an Admin closing
            // the tab must not cancel the roster's emails and notification with no retry.
            await work(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Assembly vote {VoteId}: {What} failed after the state change was committed.",
                voteId, what);
        }
    }

    /// <summary>
    /// Raises one in-app notification keyed to the vote, which closing later resolves, and
    /// emails everyone on the roster that the vote is open.
    /// </summary>
    /// <remarks>
    /// Per-recipient try/catch, as in Surveys' invite send: one bad address must not leave
    /// the rest of the electorate uninformed about a vote that is already open.
    /// </remarks>
    internal async Task NotifyRosterOpenedAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct)
    {
        // The alert goes out before the emails: a stop or cancel mid-send resolves notifications
        // by source key, and a row that does not exist yet cannot be resolved. It is its own
        // post-transition step so a Notifications fault cannot also cost the roster its emails.
        await AfterTransitionAsync(
            "alerting the roster that the vote opened", vote.Id,
            token => EmitOpenedAlertAsync(vote, roster, token));

        await SendOpenedEmailsAsync(vote, roster, ct);
    }

    /// <summary>
    /// The in-app "cast your ballot" alert for a vote that has just opened. Emitted before
    /// the emails and only while the vote still takes ballots; see the caller for why.
    /// </summary>
    private async Task EmitOpenedAlertAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct)
    {
        var userIds = roster.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList();
        if (userIds.Count > 0
            && await repository.GetByIdAsync(vote.Id, ct) is { } open
            && open.AcceptsBallotsAt(clock.GetCurrentInstant()))
        {
            try
            {
                var people = await users.GetUserInfosAsync(userIds, ct);
                userIds = userIds.Select(id => people.GetValueOrDefault(id)?.Id ?? id).Distinct().ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Assembly vote {VoteId}: recipient lookup failed; alerting the stored roster IDs", vote.Id);
            }

            await notifications.SendAsync(
                GovernanceNotificationSources.AssemblyVoteOpened,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                vote.Title.Resolve(vote.OfficialCulture, vote.OfficialCulture),
                userIds,
                actionUrl: VoteUrl(vote.Id),
                sourceKey: vote.Id.ToString(),
                cancellationToken: ct);

            // A stop or cancel landing between the check and the send resolves a key with no rows
            // yet, and no transaction spans two sections: re-read and resolve again (idempotent).
            if (await repository.GetByIdAsync(vote.Id, ct) is not { } after
                || !after.AcceptsBallotsAt(clock.GetCurrentInstant()))
            {
                await ClearOpenNotificationAsync(vote, null, ct);
            }
        }
    }

    /// <summary>
    /// Emails the roster that the vote is open, stamping every row of each human reached; an
    /// unstamped row is retried by the hourly sweep. Retry batches audit completed deliveries
    /// before propagating interruption.
    /// Takes the whole roster so a human with a stamped row is not mailed again through another.
    /// </summary>
    private async Task SendOpenedEmailsAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct,
        ICollection<Exception>? failures = null)
    {
        var recipients = (await RecipientsAsync(roster, ct))
            .Where(r => r.Rows.All(row => row.NotifiedAt is null))
            .ToList();
        var notified = 0;

        try
        {
            foreach (var (rosterRow, rows, info, address, language) in recipients)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Re-read per recipient, with the clock: a stop, cancel, extend or lapse mid-batch
                    // ends it rather than mailing the rest a deadline — or a vote — that no longer stands.
                    var sentAt = clock.GetCurrentInstant();
                    var current = await repository.GetByIdAsync(vote.Id, ct);
                    if (current is null || !current.AcceptsBallotsAt(sentAt)) break;
                    var closesAt = AssemblyVotePresentation.ClosingLocal(current.ClosesAt);

                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await email.SendAsync(
                            WithRosterDeliveryId(messages.AssemblyVoteOpened(
                                info.Id,
                                address,
                                info.BurnerName,
                                EmailTitle(current, language),
                                closesAt,
                                rosterRow.IsOfficial,
                                VoteUrl(vote.Id),
                                language), rows, current.ClosesAt),
                            CancellationToken.None);

                        // Stamped per row immediately, so a batch dying halfway does not earn the whole
                        // roster a duplicate; conditional on the deadline this email named.
                        await repository.StampNotifiedAsync(
                            rows.Select(r => r.Id).ToList(), clock.GetCurrentInstant(), current.ClosesAt, CancellationToken.None);
                        notified++;
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new InvalidOperationException(ex.Message, ex);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures?.Add(ex);
                    logger.LogError(ex,
                        "Failed to send the vote-opened email for assembly vote {VoteId} to roster row {RosterId}.",
                        vote.Id, rosterRow.Id);
                }
            }
        }
        finally
        {
            // Retry calls collect failures and owe a job audit, including interrupted batches.
            if (failures is not null && notified > 0)
                await audit.LogAsync(
                    GovernanceAuditActions.AssemblyVoteOpened, AuditEntityTypes.AssemblyVote, vote.Id,
                    $"Re-sent the opening email for assembly vote {vote.Id} to {notified} roster member(s) "
                    + "the first send never reached.", lapseJobName);
        }
    }

    // Row order cannot change retry identity; extending the announced deadline must produce a new delivery.
    private static EmailMessage WithRosterDeliveryId(
        EmailMessage message, IReadOnlyList<AssemblyVoteRoster> rows, Instant closesAt)
    {
        var rosterId = rows.Min(row => row.Id);
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{message.TemplateName}:{rosterId:N}:{closesAt.ToUnixTimeTicks()}");
        return message with
        {
            DeliveryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16))
        };
    }

    /// <summary>
    /// Bounded for the email subject (varchar(1000)): only bites on a draft written before
    /// title validation existed, where it would otherwise throw after the vote opened.
    /// </summary>
    private string EmailTitle(AssemblyVote vote, string language) =>
        Truncate(vote.Title.Resolve(language, vote.OfficialCulture), maxTitleLength);

    /// <summary>Trims to <paramref name="max"/> characters, ellipsis included in the count.</summary>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var length = max - 1;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return string.Concat(text.AsSpan(0, length), "\u2026");
    }

    internal async Task NotifyRosterCancelledAsync(AssemblyVote vote, CancellationToken ct)
    {
        var roster = await repository.GetRosterAsync(vote.Id, ct);
        var recipients = await RecipientsAsync(roster, ct);

        foreach (var (rosterRow, _, info, address, language) in recipients)
        {
            try
            {
                await email.SendAsync(
                    messages.AssemblyVoteCancelled(
                        info.Id,
                        address,
                        info.BurnerName,
                        EmailTitle(vote, language),
                        vote.CancelReason ?? string.Empty,
                        language),
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to send the vote-cancelled email for assembly vote {VoteId} to roster row {RosterId}.",
                    vote.Id, rosterRow.Id);
            }
        }
    }

    /// <summary>
    /// One recipient per human on the roster, with every row that resolved to them; rows with no
    /// address drop out. Always fed the whole roster: the decision to mail is taken over the group.
    /// </summary>
    /// <remarks>
    /// Users resolves a merged-away id forward to the survivor, so the member is mailed where they
    /// read. Every row in a group is stamped, or that member is mailed again next sweep.
    /// </remarks>
    private async Task<List<(AssemblyVoteRoster Roster, IReadOnlyList<AssemblyVoteRoster> Rows, UserInfo Info, string Address, string Language)>>
        RecipientsAsync(IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct)
    {
        var userIds = roster.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToList();
        if (userIds.Count == 0) return [];

        var infos = await users.GetUserInfosAsync(userIds, ct);

        return roster
            .Where(r => r.UserId is not null
                && infos.TryGetValue(r.UserId.Value, out var info) && !string.IsNullOrWhiteSpace(info.Email))
            .GroupBy(r => infos[r.UserId!.Value].Id)
            .Select(g =>
            {
                var representative = g.FirstOrDefault(r => r.IsOfficial) ?? g.First();
                var info = infos[representative.UserId!.Value];
                return (
                    representative,
                    (IReadOnlyList<AssemblyVoteRoster>)g.ToList(),
                    info,
                    info.Email!,
                    info.PreferredLanguage.IsSupportedCultureCode()
                        ? info.PreferredLanguage : CultureCatalog.DefaultCultureCode);
            })
            .ToList();
    }

    private static string VoteUrl(Guid voteId) =>
        $"/Governance/Votes/{voteId}";

    /// <summary>
    /// Re-sends the vote-opened email to roster rows it never reached. The Open transition is
    /// committed before the emails go out, so a send that threw informs nobody and nothing
    /// else ever looks at the unstamped row — an electorate that does not know the vote exists.
    /// </summary>
    /// <remarks>
    /// A row with no reachable address — anonymized, or no notification email — is never
    /// stamped and so is retried every sweep. That costs one in-memory roster read while the
    /// vote is open, and a row that becomes reachable gets the email it missed.
    /// </remarks>
    internal async Task RetryOpenedEmailsAsync(
        AssemblyVote vote, ICollection<Exception> failures, CancellationToken ct)
    {
        if (vote.Status != AssemblyVoteStatus.Open) return;

        var roster = await repository.GetRosterAsync(vote.Id, ct);
        if (roster.All(r => r.NotifiedAt is not null)) return;

        // The whole roster, not the unstamped rows: a human holding a stamped row and an
        // unstamped one was reached, and the send decides that over all their rows.
        await SendOpenedEmailsAsync(vote, roster, ct, failures);
    }

    /// <summary>
    /// Sends the T-24h reminder to roster members who have not voted, stamping each row so
    /// the reminder cannot repeat however often the job runs, and auditing the batch that
    /// was delivered under the job actor.
    /// </summary>
    internal async Task SendRemindersAsync(Instant now, ICollection<Exception> failures, CancellationToken ct)
    {
        var due = await repository.GetOpenVotesClosingBetweenAsync(now, now + ReminderLeadTime, ct);

        foreach (var vote in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SendVoteRemindersAsync(vote, failures, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex, "Failed to prepare assembly vote {VoteId} reminders.", vote.Id);
            }
        }
    }

    private async Task SendVoteRemindersAsync(
        AssemblyVote vote, ICollection<Exception> failures, CancellationToken ct)
    {
        var pending = await repository.GetRosterNeedingReminderAsync(vote.Id, ct);
        if (pending.Count == 0) return;

        // Grouped over the whole roster: a human with one voted or reminded row and one blank
        // row is left alone, or a member who voted is told they have not.
        var pendingIds = pending.Select(r => r.Id).ToHashSet();
        var recipients = (await RecipientsAsync(await repository.GetRosterAsync(vote.Id, ct), ct))
            .Where(r => r.Rows.All(row => pendingIds.Contains(row.Id)))
            .ToList();
        var reminded = new List<Guid>(recipients.Count);

        try
        {
            foreach (var (rosterRow, rows, info, address, language) in recipients)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Eligibility is re-read per recipient, not trusted from the list: sending
                    // the whole roster takes long enough for somebody to vote, or for an Admin to
                    // stop, cancel or extend the vote, and "you have not voted and the vote closes
                    // soon" is wrong in each case — all but the first for everybody left.
                    var current = await repository.GetByIdAsync(vote.Id, ct);
                    if (current is null || current.Status != AssemblyVoteStatus.Open) break;

                    // The clock per recipient, read after the vote: a lapse mid-batch stops the
                    // mailing, and an Extend committed before this read cannot clear this stamp.
                    var sentAt = clock.GetCurrentInstant();

                    // The deadline from that same read: an Extend mid-batch moves it, and the rest of
                    // the roster is reminded when the new deadline comes into range.
                    if (current.ClosesAt <= sentAt || current.ClosesAt > sentAt + ReminderLeadTime) break;
                    var closesAt = AssemblyVotePresentation.ClosingLocal(current.ClosesAt);

                    // Over every row the human holds: a ballot cast mid-batch lands on whichever
                    // of their rows EffectiveRosterAsync resolved them to.
                    if (await HasBallotOnAnyRowAsync(vote.Id, rows, ct))
                    {
                        continue;
                    }

                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await email.SendAsync(
                            WithRosterDeliveryId(messages.AssemblyVoteReminder(
                                info.Id,
                                address,
                                info.BurnerName,
                                EmailTitle(current, language),
                                closesAt,
                                rosterRow.IsOfficial,
                                VoteUrl(vote.Id),
                                language), rows, current.ClosesAt),
                            CancellationToken.None);

                        // Stamped per row immediately, conditional on the deadline this email named.
                        await repository.StampReminderSentAsync(
                            rows.Select(r => r.Id).ToList(), sentAt, current.ClosesAt, CancellationToken.None);
                        // One entry per human, not per row: every row of a merge chain is stamped,
                        // but the member behind them got one email and the audit count says members.
                        reminded.Add(rosterRow.Id);
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new InvalidOperationException(ex.Message, ex);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    logger.LogError(ex,
                        "Failed to send the vote reminder for assembly vote {VoteId} to roster row {RosterId}.",
                        vote.Id, rosterRow.Id);
                }
            }
        }
        finally
        {
            if (reminded.Count > 0)
            {
                // The count actually delivered: a send that threw is unstamped and retried.
                await audit.LogAsync(
                    GovernanceAuditActions.AssemblyVoteRemindersSent, AuditEntityTypes.AssemblyVote, vote.Id,
                    $"Sent the closing reminder for assembly vote {vote.Id} to {reminded.Count} roster member(s).",
                    lapseJobName);
            }
        }
    }

    private async Task<bool> HasBallotOnAnyRowAsync(
        Guid voteId, IReadOnlyList<AssemblyVoteRoster> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            if (await repository.GetBallotForRosterAsync(voteId, row.Id, ct) is not null) return true;
        }
        return false;
    }

}
