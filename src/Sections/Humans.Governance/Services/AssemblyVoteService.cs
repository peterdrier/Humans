using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace Humans.Governance.Services;

/// <summary>
/// Implements <see cref="IAssemblyVoteService"/>. See that interface for the embargo
/// contract — this class is where it is kept.
/// </summary>
internal sealed class AssemblyVoteService(
    IAssemblyVoteRepository repository,
    IApplicationRepository applications,
    IRoleAssignmentService roleAssignments,
    ITeamServiceRead teams,
    IUserServiceRead users,
    IUserEmailService userEmails,
    IEmailService email,
    IEmailMessageFactory messages,
    INotificationEmitter notifications,
    INotificationAutoResolve notificationResolve,
    IAuditLogService audit,
    IGoogleTranslationService translation,
    IClock clock,
    ILogger<AssemblyVoteService> logger)
    : IAssemblyVoteService, IUserDataContributor, IUserMerge
{
    /// <summary>The recurring job that closes lapsed votes and sends the T-24h reminder.</summary>
    internal const string LapseJobName = "governance-assembly-vote-lapse";

    // Every bounded text column this section writes, mirrored from the EF configurations, in
    // one place. Text arriving from a form is checked with Fits before it reaches the
    // DbContext, so an over-long value is a rejected draft or action rather than a 500 from
    // PostgreSQL. A new bounded column belongs in this block and in the check that guards it;
    // the localized JSON columns (OfficialText, option Label) are unbounded by design.
    //
    // Title is the exception: the column is jsonb and uncapped, but the title is copied into
    // bounded columns elsewhere — the email outbox Subject (varchar(1000)) and Notification
    // Title (varchar(200)) — and those writes happen after a transition that cannot be undone.
    // A title is bounded at the draft so the Board hears about it while the vote is still
    // editable; the sends below still trim, for drafts written before this bound existed.
    internal const int MaxTitleLength = 200;         // assembly_votes.Title, per culture
    internal const int MaxOptionKeyLength = 100;     // assembly_vote_options.Key
    internal const int MaxCancelReasonLength = 4000; // assembly_votes.CancelReason
    internal const int MaxInfoUrlLength = 2000;      // assembly_votes.InfoUrl
    internal const int MaxCultureLength = 10;        // assembly_votes.OfficialCulture

    /// <summary>Whether a value fits the column that will store it. Blank always fits.</summary>
    private static bool Fits(string? value, int max) => (value?.Length ?? 0) <= max;

    /// <summary>
    /// Mirrors <c>audit_log.description</c>. A near-maximum cancellation reason still fits
    /// its own column while the audit description, which prefixes it, would not — and
    /// AuditLogService swallows that failure, so the cancellation would go unaudited.
    /// Trimmed here for the same reason the notification title is: a neighbouring section's
    /// column width does not belong in Governance's validation rules.
    /// </summary>
    private const int MaxAuditDescriptionLength = 4000;

    /// <summary>How far ahead of closing the reminder goes out.</summary>
    private static readonly Duration ReminderLeadTime = Duration.FromHours(24);

    /// <summary>
    /// How long a Closed vote with no stored tally is left alone before a read treats it as
    /// interrupted and finishes it. Long enough that a close still running its own tail is
    /// never mistaken for a dead one; short enough that a real interruption is repaired by
    /// the next read or the next hourly sweep.
    /// </summary>
    private static readonly Duration InterruptedCloseGrace = Duration.FromMinutes(5);

    /// <summary>
    /// Enums serialize as names so a stored result stays readable and survives any later
    /// reordering of the CLR enums. NodaTime is configured because
    /// <see cref="AssemblyVoteResult.ComputedAt"/> is an <see cref="Instant"/>, which the
    /// default converters do not understand — without this every stored result would read
    /// back with <c>ComputedAt = Instant.MinValue</c>, the same trap
    /// <c>VolunteerBuildStatusConfiguration</c> documents.
    /// </summary>
    private static readonly JsonSerializerOptions ResultJsonOptions =
        new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }
            .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    // ==========================================================================
    // Member-facing reads
    // ==========================================================================

    public async Task<IReadOnlyList<AssemblyVoteListItem>> GetVotesForMemberAsync(
        Guid userId, CancellationToken ct = default)
    {
        var votes = await SettleAllAsync(ct);
        var items = new List<AssemblyVoteListItem>(votes.Count);

        // A draft is the Board's authoring surface: unannounced, still being written, and
        // deletable. Members see a vote from the moment it opens, never before.
        foreach (var vote in votes.Where(v => v.Status != AssemblyVoteStatus.Draft))
        {
            var roster = await repository.GetRosterRowAsync(vote.Id, userId, ct);
            var ballot = roster is null
                ? null
                : await repository.GetBallotForRosterAsync(vote.Id, roster.Id, ct);

            items.Add(new AssemblyVoteListItem(
                vote.Id,
                Resolve(vote.Title, vote.OfficialCulture),
                vote.Status,
                vote.Kind,
                vote.ClosesAt,
                vote.ClosedAt,
                IsOnRoster: roster is not null,
                IsOfficial: roster?.IsOfficial ?? false,
                HasBallot: ballot is not null,
                await repository.GetParticipationAsync(vote.Id, ct)));
        }

        // Open votes first, then most recently closing. A few rows a year — ordering in
        // memory is cheaper to read than to express in SQL.
        return items
            .OrderByDescending(i => i.Status == AssemblyVoteStatus.Open)
            .ThenByDescending(i => i.ClosesAt)
            .ToList();
    }

    public async Task<AssemblyVoteDetail?> GetVoteForMemberAsync(
        Guid voteId, Guid userId, CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return null;
        // Same rule as the list: a draft's id is not a back door to its official text.
        if (vote.Status == AssemblyVoteStatus.Draft) return null;

        var roster = await repository.GetRosterRowAsync(voteId, userId, ct);
        var ballot = roster is null
            ? null
            : await repository.GetBallotForRosterAsync(voteId, roster.Id, ct);

        return await DetailAsync(vote, roster, ballot, ct);
    }

    /// <summary>
    /// Builds the page model shared by the vote page and the results page. Carries the
    /// viewer's own ballot but never anyone else's, and never a tally.
    /// </summary>
    private async Task<AssemblyVoteDetail> DetailAsync(
        AssemblyVote vote,
        AssemblyVoteRoster? roster,
        AssemblyBallot? ballot,
        CancellationToken ct)
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        var now = clock.GetCurrentInstant();

        return new AssemblyVoteDetail(
            vote.Id,
            Resolve(vote.Title, vote.OfficialCulture),
            Resolve(vote.OfficialText, vote.OfficialCulture),
            vote.OfficialCulture,
            // Only when a culture-specific text actually exists and is not the binding one.
            // `Resolve` falls back to the official text when the viewer's culture was never
            // authored, and warning a member that the binding text is "a translation" is the
            // opposite of what this flag is for.
            IsTranslation: vote.OfficialText.HasCulture(culture)
                           && !culture.Equals(vote.OfficialCulture, StringComparison.OrdinalIgnoreCase),
            vote.InfoUrl,
            vote.Kind,
            vote.RequiredMajority,
            vote.IndicativeAudience,
            vote.BallotDisclosure,
            vote.Status,
            vote.ClosesAt,
            vote.ClosedAt,
            vote.AssemblyDate,
            vote.CancelReason,
            IsOnRoster: roster is not null,
            IsOfficial: roster?.IsOfficial ?? false,
            CanCastBallot: roster is not null && vote.AcceptsBallotsAt(now),
            vote.Options
                .OrderBy(o => o.Order)
                .Select(o => new AssemblyVoteOptionView(
                    o.Key, Resolve(o.Label, vote.OfficialCulture), o.Order))
                .ToList(),
            ballot is null ? null : BallotView(ballot),
            await repository.GetParticipationAsync(vote.Id, ct));
    }

    private static AssemblyBallotView BallotView(AssemblyBallot ballot) =>
        new(ballot.Choice,
            ballot.Ranking,
            ballot.Revision,
            ballot.CastAt,
            ballot.UpdatedAt,
            ballot.History
                .OrderBy(h => h.Revision)
                .Select(h => new AssemblyBallotRevisionView(
                    h.Revision, h.Choice, h.Ranking, h.RecordedAt))
                .ToList());

    // ==========================================================================
    // Casting a ballot
    // ==========================================================================

    public async Task<BallotSubmissionOutcome> CastBallotAsync(
        Guid voteId,
        Guid userId,
        AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking,
        CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return BallotSubmissionOutcome.NotFound;

        var now = clock.GetCurrentInstant();
        if (!vote.AcceptsBallotsAt(now)) return BallotSubmissionOutcome.VoteNotOpen;

        // Entitlement comes from the frozen roster, never from the member's tier today: a
        // member who lost their term mid-vote keeps the ballot the roster granted them.
        var roster = await repository.GetRosterRowAsync(voteId, userId, ct);
        if (roster is null) return BallotSubmissionOutcome.NotOnRoster;

        if (!TryNormalizeBallot(vote, choice, ranking, out var normalizedRanking))
        {
            return BallotSubmissionOutcome.InvalidBallot;
        }

        var ballot = await repository.UpsertBallotAsync(
            voteId, roster.Id, choice, normalizedRanking, now, ct);

        // The vote closed while this submission was in flight. Nothing was written, and the
        // member is told so rather than being shown a "recorded" they would not find in the
        // published result.
        if (ballot is null) return BallotSubmissionOutcome.VoteNotOpen;

        // The action distinguishes a first cast from a change, read from the revision the
        // locked upsert assigned rather than from a ballot read before it: two first ballots
        // for the same roster row can both see none, and the second one is a change.
        // Neither entry records what was chosen. That is the whole point — the audit log is
        // widely readable.
        //
        // The entity is the vote, never the ballot. An audit row keeps its ActorUserId for
        // good (AuditLogService.EraseForUserAsync is a no-op by design), so naming the ballot
        // id here would leave a permanent join from an erased person to the row holding their
        // Choice and Ranking — which is exactly what erasure promises to sever.
        await audit.LogAsync(
            ballot.Revision == 1 ? AuditAction.AssemblyBallotCast : AuditAction.AssemblyBallotChanged,
            AuditEntityTypes.AssemblyVote,
            voteId,
            ballot.Revision == 1
                ? $"Cast a ballot on assembly vote {voteId} (revision {ballot.Revision})."
                : $"Changed their ballot on assembly vote {voteId} (revision {ballot.Revision}).",
            userId);

        return BallotSubmissionOutcome.Recorded;
    }

    /// <summary>
    /// Validates a submitted ballot against the vote's kind and options, and reduces a
    /// ranked ballot to the ordered key list the counting rules expect.
    /// </summary>
    /// <remarks>
    /// Rejects a choice that does not belong to the vote's kind, an unknown option key, and
    /// a duplicate key. A partial ranking is accepted — unranked options are simply never
    /// preferred — and so is an Abstain on a ranked vote.
    /// </remarks>
    private static bool TryNormalizeBallot(
        AssemblyVote vote,
        AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking,
        out IReadOnlyList<string>? normalized)
    {
        normalized = null;

        if (vote.Kind == AssemblyVoteKind.YesNo)
        {
            return choice is AssemblyBallotChoice.Yes
                or AssemblyBallotChoice.No
                or AssemblyBallotChoice.Abstain;
        }

        if (choice == AssemblyBallotChoice.Abstain) return true;
        if (choice != AssemblyBallotChoice.Ranked) return false;
        if (ranking is null || ranking.Count == 0) return false;

        var keys = vote.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        if (ranking.Any(k => !keys.Contains(k))) return false;
        if (ranking.Distinct(StringComparer.Ordinal).Count() != ranking.Count) return false;

        normalized = ranking;
        return true;
    }

    private static string Resolve(GovernanceLocalizedText text, string officialCulture) =>
        text.Resolve(CultureInfo.CurrentUICulture.Name, officialCulture);

    // ==========================================================================
    // Settling state — the inline close that makes the deadline real
    // ==========================================================================

    /// <summary>
    /// Loads a vote and closes it first if its deadline has passed, so no caller can ever
    /// observe an Open vote that should be Closed.
    /// </summary>
    private async Task<AssemblyVote?> SettleAsync(Guid voteId, CancellationToken ct)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return null;

        return await SettleOneAsync(vote, ct);
    }

    /// <summary>
    /// Brings one already-loaded vote up to date: closes it if its deadline has passed, and
    /// finishes a close that was interrupted after its status went down. Returns the vote as
    /// it now stands — which is not always the one passed in, because a close that loses to
    /// somebody else's transition leaves this object stamped Closed in memory only.
    /// </summary>
    private async Task<AssemblyVote> SettleOneAsync(AssemblyVote vote, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (vote.Status != AssemblyVoteStatus.Open || now < vote.ClosesAt)
        {
            await FinishInterruptedCloseAsync(vote, ct);
            return vote;
        }

        if (await CloseAsync(vote, closedByUserId: null, AuditAction.AssemblyVoteClosed,
                $"Assembly vote {vote.Id} closed automatically at its announced time.", ct))
        {
            return vote;
        }

        // The close was refused: an Extend moved the deadline, or another close landed first.
        // CloseAsync has already stamped this object Closed, so handing it back would have the
        // rest of the request treat a vote that is still open as closed — a ballot refused,
        // the form hidden. The row is the truth.
        return await repository.GetByIdAsync(vote.Id, ct) ?? vote;
    }

    /// <summary>
    /// Closure writes the status first and everything else second (see <see cref="CloseAsync"/>).
    /// A process that died in between left a vote that is closed but has no result, no retired
    /// notification and no audit entry; whoever looks next finishes all three. Who closed it is
    /// already persisted, so the replay attributes it exactly as the original would have.
    /// </summary>
    private async Task FinishInterruptedCloseAsync(AssemblyVote vote, CancellationToken ct)
    {
        if (vote.Status != AssemblyVoteStatus.Closed || vote.ResultJson is not null) return;

        // A close that committed its status seconds ago is not interrupted, it is running:
        // its own tail sits between the status write and the stored tally right now. Replaying
        // that would duplicate the audit entry and the notification retirement of a close
        // nobody interrupted, so a result-less close is taken over only once it has stood
        // long enough that no request could still be finishing it.
        var readAt = vote.UpdatedAt;
        if (clock.GetCurrentInstant() < readAt + InterruptedCloseGrace) return;

        // And two readers arriving after that are still two. Bumping UpdatedAt from the value
        // each of them read is the claim: exactly one write lands, and the loser leaves the
        // tail to the winner.
        vote.UpdatedAt = clock.GetCurrentInstant();
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Closed, readAt, ct)) return;

        await FinishCloseAsync(
            vote,
            vote.ClosedByUserId,
            vote.ClosedByUserId is null ? AuditAction.AssemblyVoteClosed : AuditAction.AssemblyVoteStopped,
            vote.ClosedByUserId is null
                ? $"Assembly vote {vote.Id} closed automatically at its announced time."
                : $"Stopped assembly vote {vote.Id} before its announced closing time.",
            ct);
    }

    private async Task<IReadOnlyList<AssemblyVote>> SettleAllAsync(CancellationToken ct)
    {
        var votes = await repository.GetAllAsync(ct);

        var settled = new List<AssemblyVote>(votes.Count);
        foreach (var vote in votes)
        {
            settled.Add(await SettleOneAsync(vote, ct));
        }

        return settled;
    }

    /// <summary>
    /// The single close path: computes the result once, stores it on the vote, resolves the
    /// open-vote notification, and audits. Every way a vote can close funnels through here
    /// so a closed vote always has exactly one stored result.
    /// </summary>
    private async Task<bool> CloseAsync(
        AssemblyVote vote,
        Guid? closedByUserId,
        AuditAction action,
        string description,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();

        vote.Status = AssemblyVoteStatus.Closed;
        // A lapsed vote closed at its announced time, whenever the sweep or the next read
        // happened to notice. BuildActa prints ClosedAt into the legal summary, so stamping
        // the observation instant would have the acta claim a vote ran late. An Admin Stop
        // really does close it now.
        vote.ClosedAt = closedByUserId is null ? vote.ClosesAt : now;
        vote.ClosedByUserId = closedByUserId;
        vote.UpdatedAt = now;

        // Order matters, and it is the only thing standing between a late ballot and a tally
        // that silently omits it. The status is persisted *before* the ballots are read: from
        // that commit on, UpsertBallotAsync re-reads the vote and refuses, so every ballot the
        // system accepted is already in the table by the time counting looks. Computing first
        // and flipping second would leave the whole counting interval open for a submission to
        // slip in behind the snapshot, be told "recorded", and never appear in the result.
        // Refused means the row is no longer Open — somebody else finished this vote first —
        // or an Extend moved the deadline this lapse was working from. Either way the other
        // actor owns the close and nothing here should run, including the audit entry and the
        // notification: the tail belongs to whoever's status write landed.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, ct: ct)) return false;

        await FinishCloseAsync(vote, closedByUserId, action, description, ct);
        return true;
    }

    /// <summary>
    /// Everything a close owes after its status is down: the retired open-vote notification,
    /// the audit entry, and the stored tally. Separate from <see cref="CloseAsync"/> because
    /// the status write commits first, and anything that throws after it leaves a closed vote
    /// owing all three — <see cref="SettleAsync"/> replays this, not just the result.
    /// <para>
    /// The stored result is written <em>last</em> because its absence is what the replay
    /// looks for. Storing it first would mark the close complete while the notification and
    /// the audit entry were still owed, and a process that stopped there would leave a
    /// permanently unaudited close, which the terminal-state check then refuses to redo. In
    /// this order the worst a crash can cost is a repeated close entry on the next read —
    /// a duplicate in the audit log is visible and explicable; a missing one is neither.
    /// </para>
    /// </summary>
    private async Task FinishCloseAsync(
        AssemblyVote vote,
        Guid? closedByUserId,
        AuditAction action,
        string description,
        CancellationToken ct)
    {
        await ClearOpenNotificationAsync(vote, closedByUserId, ct);

        if (closedByUserId is { } actor)
        {
            await audit.LogAsync(action, AuditEntityTypes.AssemblyVote, vote.Id, description, actor);
        }
        else
        {
            await audit.LogAsync(action, AuditEntityTypes.AssemblyVote, vote.Id, description, LapseJobName);
        }

        await StoreResultAsync(vote, ct);
    }

    /// <summary>
    /// Computes the vote's tally and stores it on the already-closed vote. Split out of
    /// <see cref="CloseAsync"/> so the status can be persisted first, and reused by the
    /// settle path to finish a close that was interrupted after the status went down.
    /// </summary>
    private async Task StoreResultAsync(AssemblyVote vote, CancellationToken ct)
    {
        var result = await ComputeResultAsync(vote, ct);
        vote.ResultJson = JsonSerializer.Serialize(result, ResultJsonOptions);

        // Closed is the state this write expects and the one it leaves: the tally lands on the
        // row the close already stamped, and a vote cancelled out from under it gets nothing.
        await repository.UpdateAsync(vote, AssemblyVoteStatus.Closed, ct: ct);
    }

    /// <summary>
    /// Retires the open-vote notification for a vote that has just ended, best-effort.
    /// <para>
    /// Every caller reaches this <em>after</em> persisting a terminal status, and terminal
    /// means nothing re-enters that path — so a throw from here would strand whatever the
    /// caller still had to do (the closure audit, the cancellation audit and its roster
    /// email) with no retry, permanently. That is worth more than a tidy notification
    /// meter, so the failure is logged and swallowed. Both `CloseAsync` and `CancelAsync`
    /// lost exactly this, one round apart; it lives here now so a third end-of-vote path
    /// cannot lose it again.
    /// </para>
    /// </summary>
    private Task ClearOpenNotificationAsync(
        AssemblyVote vote, Guid? actorUserId, CancellationToken _) =>
        AfterTransitionAsync(
            "resolving the open-vote notification", vote.Id,
            ct => notificationResolve.ResolveBySourceKeyAsync(
                NotificationSource.AssemblyVoteOpened, vote.Id.ToString(), actorUserId, ct));

    /// <summary>
    /// Runs a side effect that happens <em>after</em> a committed state change, without
    /// letting it undo one. Open / Stop / Cancel are irreversible and have no retry path, so
    /// a throw from notification or email work leaves the vote in its new state with the
    /// audit entry, the roster email or the notification permanently missing.
    /// <para>
    /// Every such call goes through here. Three separate paths lost this one at a time —
    /// the close audit, the cancel audit and roster email, then recipient resolution on both
    /// — so the guarantee lives in one place rather than in a try/catch per call site that
    /// the fourth path will forget.
    /// </para>
    /// </summary>
    private async Task AfterTransitionAsync(
        string what, Guid voteId, Func<CancellationToken, Task> work)
    {
        try
        {
            // Deliberately not the request's token. The state change is committed and
            // irreversible, and the roster's email is part of it being real: an Admin who
            // closes the tab after clicking Open would otherwise cancel the recipient lookup,
            // the emails and the notification, and nothing ever retries them.
            await work(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Assembly vote {VoteId}: {What} failed after the state change was committed.",
                voteId, what);
        }
    }

    // ==========================================================================
    // Counting
    // ==========================================================================

    /// <summary>
    /// Counts the vote's ballots into a storable result: the official audience always, the
    /// indicative audience separately when the vote has one. The two are never merged.
    /// </summary>
    private async Task<AssemblyVoteResult> ComputeResultAsync(AssemblyVote vote, CancellationToken ct)
    {
        var roster = await repository.GetRosterAsync(vote.Id, ct);
        var ballots = await repository.GetBallotsAsync(vote.Id, ct);
        var byRoster = ballots.ToDictionary(b => b.RosterId);

        var official = Tally(vote, roster.Where(r => r.IsOfficial).ToList(), byRoster);
        var indicative = vote.IndicativeAudience == IndicativeAudience.None
            ? null
            : Tally(vote, roster.Where(r => !r.IsOfficial).ToList(), byRoster);

        return new AssemblyVoteResult(
            official,
            indicative,
            vote.Kind == AssemblyVoteKind.YesNo
                ? AssemblyVoteMethod.YesNo
                : AssemblyVoteMethod.InstantRunoff,
            clock.GetCurrentInstant());
    }

    private static AssemblyVoteAudienceResult Tally(
        AssemblyVote vote,
        IReadOnlyList<AssemblyVoteRoster> audience,
        Dictionary<Guid, AssemblyBallot> ballotsByRoster)
    {
        var cast = audience
            .Select(r => ballotsByRoster.GetValueOrDefault(r.Id))
            .OfType<AssemblyBallot>()
            .Select(b => new AssemblyVoteCounting.CountableBallot(b.Choice, b.Ranking))
            .ToList();

        if (vote.Kind == AssemblyVoteKind.YesNo)
        {
            var (tally, verdict) = AssemblyVoteCounting.CountYesNo(cast, vote.RequiredMajority);
            return new AssemblyVoteAudienceResult(
                audience.Count, cast.Count, tally, null, null, verdict, []);
        }

        var (rounds, winner, rankedVerdict, notes) = AssemblyVoteCounting.CountInstantRunoff(
            cast, vote.Options.OrderBy(o => o.Order).ToList());

        return new AssemblyVoteAudienceResult(
            audience.Count, cast.Count, null, rounds, winner, rankedVerdict, notes);
    }

    // ==========================================================================
    // Results
    // ==========================================================================

    public async Task<AssemblyVoteResultsView?> GetResultsAsync(
        Guid voteId, Guid userId, bool viewerIsBoardOrAdmin, CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return null;

        // The embargo: results exist only once the vote is closed, and Board and Admin get
        // no exemption here. The audited peek is the only early window.
        if (vote.Status != AssemblyVoteStatus.Closed || vote.ResultJson is null) return null;

        var result = JsonSerializer.Deserialize<AssemblyVoteResult>(vote.ResultJson, ResultJsonOptions);
        if (result is null)
        {
            logger.LogError("Stored result for assembly vote {VoteId} could not be read.", voteId);
            return null;
        }

        var roster = await repository.GetRosterRowAsync(voteId, userId, ct);
        var ownBallot = roster is null
            ? null
            : await repository.GetBallotForRosterAsync(voteId, roster.Id, ct);

        var disclose = viewerIsBoardOrAdmin
                       || (vote.BallotDisclosure == BallotDisclosure.RosterSeesNames && roster is not null);

        var disclosed = disclose ? await DisclosureRowsAsync(voteId, ct) : null;

        if (viewerIsBoardOrAdmin)
        {
            await audit.LogAsync(
                AuditAction.AssemblyBallotsViewed, AuditEntityTypes.AssemblyVote, voteId,
                $"Viewed the individual ballots of assembly vote {voteId} on the results page.",
                userId);
        }

        return new AssemblyVoteResultsView(
            await DetailAsync(vote, roster, ownBallot, ct),
            result,
            await PeekViewsAsync(voteId, ct),
            disclosed,
            BuildActa(vote, result, await ClosedByNameAsync(vote, ct)));
    }

    /// <summary>Every standing ballot with its voter's name, for a disclosed results view.</summary>
    private async Task<IReadOnlyList<AssemblyBallotDisclosureRow>> DisclosureRowsAsync(
        Guid voteId, CancellationToken ct)
    {
        var roster = await repository.GetRosterAsync(voteId, ct);
        var ballots = await repository.GetBallotsAsync(voteId, ct);
        var byRoster = ballots.ToDictionary(b => b.RosterId);

        var userIds = roster.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList();
        var infos = await users.GetUserInfosAsync(userIds, ct);

        return roster
            .Select(r => (Roster: r, Ballot: byRoster.GetValueOrDefault(r.Id)))
            .Where(x => x.Ballot is not null)
            .Select(x => new AssemblyBallotDisclosureRow(
                x.Roster.UserId,
                x.Roster.UserId is { } id && infos.TryGetValue(id, out var info)
                    ? info.BurnerName
                    // An erased roster row keeps its ballot but loses its name — the count
                    // stays right and the person is gone, which is what Art. 17 asks for.
                    : "—",
                x.Roster.IsOfficial,
                x.Ballot!.Choice,
                x.Ballot.Ranking,
                x.Ballot.Revision))
            .OrderByDescending(r => r.IsOfficial)
            .ThenBy(r => r.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private async Task<IReadOnlyList<AssemblyVotePeekView>> PeekViewsAsync(
        Guid voteId, CancellationToken ct)
    {
        var peeks = await repository.GetPeeksAsync(voteId, ct);
        if (peeks.Count == 0) return [];

        var infos = await users.GetUserInfosAsync(
            peeks.Select(p => p.AdminUserId).Distinct().ToList(), ct);

        return peeks
            .Select(p => new AssemblyVotePeekView(
                p.AdminUserId,
                infos.TryGetValue(p.AdminUserId, out var info) ? info.BurnerName : "—",
                p.PeekedAt))
            .ToList();
    }

    /// <summary>
    /// The acta block: a plain-text summary the Secretary pastes into the minutes. Deliberately
    /// unlocalized and unstyled — it goes into a Spanish legal document, and the numbers are
    /// what statutes Art. 8.6 requires.
    /// </summary>
    /// <summary>
    /// The name of the admin who stopped the vote, for the acta. Null when the vote lapsed
    /// at its announced time (nobody closed it) or when the account no longer resolves —
    /// the acta says "an administrator" then rather than inventing a name.
    /// </summary>
    private async Task<string?> ClosedByNameAsync(AssemblyVote vote, CancellationToken ct)
    {
        if (vote.ClosedByUserId is not { } closerId) return null;
        var infos = await users.GetUserInfosAsync([closerId], ct);
        return infos.TryGetValue(closerId, out var info) ? info.BurnerName : null;
    }

    private static string BuildActa(
        AssemblyVote vote, AssemblyVoteResult result, string? closedByName)
    {
        var acta = new StringBuilder();
        var official = result.Official;

        // The counting result is keyed by the stable option key; the acta is read by people.
        // Resolved in the vote's official culture, like the title above it — which language
        // the acta as a whole is written in is a separate, open question.
        string Label(string key) =>
            vote.Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal))
                is { } option
                ? option.Label.Resolve(vote.OfficialCulture, vote.OfficialCulture)
                : key;

        acta.AppendLine(CultureInfo.InvariantCulture,
            $"Assembly vote: {vote.Title.Resolve(vote.OfficialCulture, vote.OfficialCulture)}");
        if (vote.AssemblyDate is { } date)
        {
            acta.AppendLine(CultureInfo.InvariantCulture, $"Assembly date: {date.ToInvariantDate()}");
        }

        acta.AppendLine(CultureInfo.InvariantCulture,
            $"Method: {result.Method}, majority required: {vote.RequiredMajority}");
        acta.AppendLine(CultureInfo.InvariantCulture,
            $"Electorate: {official.RosterSize}; ballots cast: {official.BallotsCast}");

        if (official.YesNo is { } yesNo)
        {
            acta.AppendLine(CultureInfo.InvariantCulture,
                $"In favour: {yesNo.Yes}; against: {yesNo.No}; abstentions: {yesNo.Abstain}");
        }
        else if (official.Rounds is { Count: > 0 } rounds)
        {
            foreach (var round in rounds)
            {
                var counts = string.Join(", ", round.Counts.Select(kv => $"{Label(kv.Key)}: {kv.Value}"));
                var eliminated = round.EliminatedKey is null
                    ? string.Empty
                    : $"; eliminated: {Label(round.EliminatedKey)}";
                acta.AppendLine(CultureInfo.InvariantCulture,
                    $"Round {round.Number}: {counts}; exhausted: {round.Exhausted}{eliminated}");
            }

            if (official.WinnerKey is { } winnerKey)
            {
                acta.AppendLine(CultureInfo.InvariantCulture, $"Winning option: {Label(winnerKey)}");
            }
        }

        acta.AppendLine(CultureInfo.InvariantCulture, $"Result: {official.Verdict}");
        if (official.Verdict == AssemblyVoteVerdict.Tie)
        {
            acta.AppendLine("Tied. Under statutes Art. 10.2 the President of the Board casts the "
                            + "deciding vote; record it here.");
        }

        // In the association's own timezone, like every closing time the electorate was
        // shown. A UTC instant here reads two hours early in summer, and the acta is the
        // document the Secretary files.
        acta.AppendLine(vote.ClosedAt is { } closedAt
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Closed at: {ClosingLocal(closedAt)} (Europe/Madrid)")
            : "Closed at: not recorded.");
        // The spec lists "who closed it" among the acta's contents, and this is the summary
        // the Secretary files: "an administrator" names nobody.
        acta.AppendLine(vote.ClosedByUserId is null
            ? "Closed automatically at the announced time."
            : closedByName is null
                ? "Closed by an administrator."
                : $"Closed by {closedByName}.");

        // No indicative line: the acta is the association's legal record of the binding vote,
        // and the spec keeps indicative ballots out of it. The results page shows them in
        // their own block, which is where an advisory number belongs.
        return acta.ToString();
    }

    // ==========================================================================
    // Drafting
    // ==========================================================================

    public async Task<IReadOnlyList<AssemblyVoteListItem>> GetAllForAdminAsync(
        CancellationToken ct = default)
    {
        var votes = await SettleAllAsync(ct);
        var items = new List<AssemblyVoteListItem>(votes.Count);

        // Every state, drafts included: this is the page Create redirects to, and its
        // Edit / Delete / Open controls are the only way a draft is ever progressed.
        foreach (var vote in votes)
        {
            items.Add(new AssemblyVoteListItem(
                vote.Id,
                Resolve(vote.Title, vote.OfficialCulture),
                vote.Status,
                vote.Kind,
                vote.ClosesAt,
                vote.ClosedAt,
                IsOnRoster: false,
                IsOfficial: false,
                HasBallot: false,
                await repository.GetParticipationAsync(vote.Id, ct)));
        }

        return items.OrderByDescending(i => i.ClosesAt).ToList();
    }

    public async Task<AssemblyVoteDraft?> GetDraftAsync(Guid voteId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return null;

        return new AssemblyVoteDraft(
            vote.Title.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            vote.OfficialText.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            vote.OfficialCulture,
            vote.InfoUrl,
            vote.Kind,
            vote.RequiredMajority,
            vote.IndicativeAudience,
            vote.BallotDisclosure,
            vote.AssemblyDate,
            vote.ClosesAt,
            vote.Options
                .OrderBy(o => o.Order)
                .Select(o => new AssemblyVoteDraftOption(
                    o.Key, o.Order, o.Label.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)))
                .ToList());
    }

    public async Task<Guid?> CreateDraftAsync(
        AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default)
    {
        if (!IsDraftValid(draft)) return null;

        var now = clock.GetCurrentInstant();
        var vote = new AssemblyVote
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = actorUserId,
            CreatedAt = now
        };

        ApplyDraft(vote, draft, now);
        foreach (var option in OptionsFor(vote.Id, draft))
        {
            vote.Options.Add(option);
        }

        await repository.AddAsync(vote, ct);
        return vote.Id;
    }

    public async Task<AssemblyVoteActionResult> UpdateDraftAsync(
        Guid voteId, AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;

        // Content is immutable once the electorate has been told what it is voting on.
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;
        if (!IsDraftValid(draft)) return AssemblyVoteActionResult.Invalid;

        ApplyDraft(vote, draft, clock.GetCurrentInstant());

        // Refused means the vote opened between the read above and this write, and content is
        // immutable from that moment — the same answer the status check above would have given.
        if (!await repository.ReplaceOptionsAsync(vote, OptionsFor(vote.Id, draft), ct))
            return AssemblyVoteActionResult.WrongState;

        return AssemblyVoteActionResult.Ok;
    }

    public async Task<int> PreFillTranslationsAsync(
        Guid voteId, IReadOnlyList<string> targetCultures, Guid actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetCultures);

        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return 0;
        // Draft-only, same reason content is: translating a motion the electorate is already
        // voting on would change what some members read mid-vote.
        if (vote.Status != AssemblyVoteStatus.Draft) return 0;

        // The revision being translated. Google's round-trip is long enough for the Board to
        // edit the draft in between, and this write carries every authored field.
        var readAt = vote.UpdatedAt;

        var source = vote.OfficialCulture;

        // Every authored text as a mutable bag, so one batched call per culture fills them all.
        var title = Copy(vote.Title);
        var officialText = Copy(vote.OfficialText);
        var options = vote.Options.Select(o => new { Option = o, Label = Copy(o.Label) }).ToList();

        List<Dictionary<string, string>> all = [title, officialText, .. options.Select(o => o.Label)];

        var filled = 0;
        foreach (var target in targetCultures.Where(
                     t => !string.Equals(t, source, StringComparison.OrdinalIgnoreCase)))
        {
            // Blanks only. Authored text is never overwritten — the Board's own wording in any
            // culture outranks a machine's, and the official culture stays the binding version.
            var pending = all.Where(d => HasText(d, source) && !HasText(d, target)).ToList();
            if (pending.Count == 0) continue;

            var translated = await translation.TranslateAsync(
                [.. pending.Select(d => d[source])], source, target, ct);
            for (var i = 0; i < pending.Count; i++)
            {
                pending[i][target] = translated[i];
            }

            filled += pending.Count;
        }

        if (filled == 0) return 0;

        vote.Title = new GovernanceLocalizedText(title);
        vote.OfficialText = new GovernanceLocalizedText(officialText);
        foreach (var o in options)
        {
            o.Option.Label = new GovernanceLocalizedText(o.Label);
        }

        vote.UpdatedAt = clock.GetCurrentInstant();

        // Refused means the draft moved on while the translations were being fetched — it
        // opened, or somebody edited it. Nothing was stored, so nothing was filled.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Draft, readAt, ct)) return 0;

        // Not audited, like the rest of draft authoring: a draft has no legal effect and the
        // Board can still rewrite every word. Open audits the content the electorate gets.
        logger.LogInformation(
            "Assembly vote {VoteId}: pre-filled {Count} missing translations from {Source} for {ActorUserId}",
            vote.Id, filled, source, actorUserId);

        return filled;

        static Dictionary<string, string> Copy(GovernanceLocalizedText text) =>
            new(text.Values, StringComparer.OrdinalIgnoreCase);

        static bool HasText(Dictionary<string, string> values, string culture) =>
            values.TryGetValue(culture, out var v) && !string.IsNullOrWhiteSpace(v);
    }

    public async Task<AssemblyVoteActionResult> DeleteDraftAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;

        // Refused means the vote opened between the read above and this delete, and an open
        // vote is never deleted — the same answer the status check above would have given.
        if (!await repository.DeleteAsync(voteId, ct)) return AssemblyVoteActionResult.WrongState;

        return AssemblyVoteActionResult.Ok;
    }

    /// <summary>
    /// A draft is publishable when it has a title and binding text in its official culture,
    /// a future closing time, and — for a ranked vote — at least two distinctly-keyed options,
    /// each labelled in the official culture. The label is what a voter reads on the ballot;
    /// a keyed-but-unlabelled option would open as a blank line on a binding vote.
    /// </summary>
    private bool IsDraftValid(AssemblyVoteDraft draft)
    {
        // Every posted enum is checked against its definition. A numeric value outside the enum
        // binds silently, and each one then falls through to the wrong default: an undefined
        // Kind is not RankedChoice, so it stores no options, yet counting treats everything
        // that is not YesNo as instant-runoff — a vote that opens with no options and results
        // in nothing. A draft is the last place to catch that, since content locks at open.
        if (!Enum.IsDefined(draft.Kind)) return false;
        if (!Enum.IsDefined(draft.RequiredMajority)) return false;
        if (!Enum.IsDefined(draft.IndicativeAudience)) return false;
        if (!Enum.IsDefined(draft.BallotDisclosure)) return false;
        if (string.IsNullOrWhiteSpace(draft.OfficialCulture)) return false;
        if (!Fits(draft.OfficialCulture, MaxCultureLength)) return false;
        if (!Fits(draft.InfoUrl, MaxInfoUrlLength)) return false;
        if (!draft.Title.TryGetValue(draft.OfficialCulture, out var title)
            || string.IsNullOrWhiteSpace(title)) return false;
        if (draft.Title.Values.Any(t => !Fits(t, MaxTitleLength))) return false;
        if (!draft.OfficialText.TryGetValue(draft.OfficialCulture, out var text)
            || string.IsNullOrWhiteSpace(text)) return false;
        if (draft.ClosesAt <= clock.GetCurrentInstant()) return false;

        if (draft.Kind != AssemblyVoteKind.RankedChoice) return true;

        // A ranked vote is counted by instant runoff, whose majority base is the ballots
        // still ranking a continuing option — a 2/3 threshold has no defined meaning there,
        // and accepting the combination stores a legal record whose acta claims a threshold
        // the count never applied. The statutes' qualified majorities are asked as YesNo
        // questions ("approve this amendment"); a ranked vote is an election.
        if (draft.RequiredMajority != RequiredMajority.Simple) return false;

        return draft.Options.Count >= 2
               && draft.Options.All(o => !string.IsNullOrWhiteSpace(o.Key)
                                         && Fits(o.Key, MaxOptionKeyLength))
               && draft.Options.All(o => o.Label.TryGetValue(draft.OfficialCulture, out var label)
                                         && !string.IsNullOrWhiteSpace(label))
               && draft.Options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count()
                  == draft.Options.Count;
    }

    private static void ApplyDraft(AssemblyVote vote, AssemblyVoteDraft draft, Instant now)
    {
        vote.Title = new GovernanceLocalizedText(draft.Title.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        vote.OfficialText = new GovernanceLocalizedText(
            draft.OfficialText.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        vote.OfficialCulture = draft.OfficialCulture;
        vote.InfoUrl = draft.InfoUrl;
        vote.Kind = draft.Kind;
        vote.RequiredMajority = draft.RequiredMajority;
        vote.IndicativeAudience = draft.IndicativeAudience;
        vote.BallotDisclosure = draft.BallotDisclosure;
        vote.AssemblyDate = draft.AssemblyDate;
        vote.ClosesAt = draft.ClosesAt;
        vote.UpdatedAt = now;
    }

    private static List<AssemblyVoteOption> OptionsFor(Guid voteId, AssemblyVoteDraft draft) =>
        draft.Kind != AssemblyVoteKind.RankedChoice
            ? []
            : draft.Options
                .Select(o => new AssemblyVoteOption
                {
                    Id = Guid.NewGuid(),
                    VoteId = voteId,
                    Order = o.Order,
                    Key = o.Key,
                    Label = new GovernanceLocalizedText(o.Label.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                })
                .ToList();

    // ==========================================================================
    // Lifecycle
    // ==========================================================================

    public async Task<AssemblyVoteActionResult> OpenAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;

        var now = clock.GetCurrentInstant();
        if (vote.ClosesAt <= now) return AssemblyVoteActionResult.Invalid;

        var roster = await BuildRosterAsync(vote, ct);
        if (roster.Count == 0) return AssemblyVoteActionResult.Invalid;

        // Building the roster reads three other sections, so the announced deadline can pass
        // while it runs. A vote that opens already lapsed is open for no time at all: the next
        // sweep closes it, having given its electorate no chance to vote.
        now = clock.GetCurrentInstant();
        if (vote.ClosesAt <= now) return AssemblyVoteActionResult.Invalid;

        vote.Status = AssemblyVoteStatus.Open;
        vote.OpenedAt = now;
        vote.OpenedByUserId = adminUserId;
        // UpdatedAt is deliberately left as read: the repository compares it against the
        // persisted row to tell a draft edit that landed while this roster was being built
        // from this open, and stamps it once the open is accepted.

        // Refused means somebody else opened this vote while its roster was being built.
        // Theirs is the roster that counts, and nothing below should run a second time.
        if (!await repository.OpenWithRosterAsync(vote, roster, ct))
            return AssemblyVoteActionResult.WrongState;

        var officialCount = roster.Count(r => r.IsOfficial);
        await audit.LogAsync(
            AuditAction.AssemblyVoteOpened, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Opened assembly vote {vote.Id}: {officialCount} official and "
            + $"{roster.Count - officialCount} indicative roster members.",
            adminUserId);

        await AfterTransitionAsync(
            "notifying the roster that the vote opened", vote.Id,
            token => NotifyRosterOpenedAsync(vote, roster, token));
        return AssemblyVoteActionResult.Ok;
    }

    /// <summary>
    /// Snapshots who may vote, at this instant, from Governance's own data: active Approved
    /// Asociado terms and the active Board role holders as official, plus the configured
    /// indicative audience.
    /// </summary>
    /// <remarks>
    /// Board members are de facto Asociados and sit on the official roster whatever their
    /// profile tier says. A person reachable through more than one source gets exactly one
    /// row, official if any source made them official.
    /// </remarks>
    private async Task<List<AssemblyVoteRoster>> BuildRosterAsync(
        AssemblyVote vote, CancellationToken ct)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;

        var asociados = await applications.GetActiveApprovedTierUserIdsAsync(
            MembershipTier.Asociado, today, ct);
        var board = await roleAssignments.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct);

        var tiers = new Dictionary<Guid, MembershipTier>();
        var official = new HashSet<Guid>();
        var indicative = new HashSet<Guid>();
        var boardMembers = board.ToHashSet();

        foreach (var id in asociados)
        {
            official.Add(id);
            tiers[id] = MembershipTier.Asociado;
        }

        foreach (var id in boardMembers)
        {
            official.Add(id);
            tiers.TryAdd(id, MembershipTier.Asociado);
        }

        if (vote.IndicativeAudience is IndicativeAudience.Colaboradores or IndicativeAudience.AllMembers)
        {
            var colaboradores = await applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Colaborador, today, ct);
            foreach (var id in colaboradores.Where(id => !official.Contains(id)))
            {
                indicative.Add(id);
                tiers.TryAdd(id, MembershipTier.Colaborador);
            }
        }

        if (vote.IndicativeAudience == IndicativeAudience.AllMembers)
        {
            var volunteers = await teams.GetTeamAsync(SystemTeamIds.Volunteers, ct);
            foreach (var member in volunteers?.Members ?? [])
            {
                if (official.Contains(member.UserId) || indicative.Contains(member.UserId)) continue;
                indicative.Add(member.UserId);
                tiers.TryAdd(member.UserId, MembershipTier.Volunteer);
            }
        }

        // A suspended or deleted account keeps no entitlement: the roster is who could vote
        // when it was frozen, and an inactive account could not.
        var candidates = official.Concat(indicative).ToList();
        var infos = await users.GetUserInfosAsync(candidates, ct);

        return candidates
            .Where(id => infos.TryGetValue(id, out var info) && info.State == UserState.Active)
            .Select(id => new AssemblyVoteRoster
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                UserId = id,
                Tier = tiers.GetValueOrDefault(id, MembershipTier.Volunteer),
                IsBoardMember = boardMembers.Contains(id),
                IsOfficial = official.Contains(id)
            })
            .ToList();
    }

    public async Task<AssemblyVoteActionResult> StopAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        // Settle first: past ClosesAt the vote is already closed, and stopping it then would
        // attribute a lapse to the admin who happened to click.
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        // Refused means the vote reached a terminal state first — its own lapse, or another
        // admin's stop. The vote is closed either way, but not by this request.
        if (!await CloseAsync(vote, adminUserId, AuditAction.AssemblyVoteStopped,
                $"Stopped assembly vote {vote.Id} before its announced closing time.", ct))
        {
            return AssemblyVoteActionResult.WrongState;
        }

        return AssemblyVoteActionResult.Ok;
    }

    public async Task<AssemblyVoteActionResult> ExtendAsync(
        Guid voteId, Instant newClosesAt, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        // Only later. Shortening a vote the electorate has already been given a deadline for
        // is what Stop is for, and Stop is explicit about being a stop.
        if (newClosesAt <= vote.ClosesAt) return AssemblyVoteActionResult.Invalid;

        var previous = vote.ClosesAt;
        // The revision this extension was built from. Two admins extending at once both
        // validate against the deadline they read, so without this the shorter extension
        // commits second and moves the deadline the electorate was just given backwards —
        // and its audit entry names a previous value that was never the persisted one.
        var readAt = vote.UpdatedAt;
        vote.ClosesAt = newClosesAt;
        vote.UpdatedAt = clock.GetCurrentInstant();

        // The vote closed, or was extended by somebody else, between the read above and this
        // write. Nothing was extended, and "only an open vote can be extended" is the honest
        // answer: the admin re-reads the new deadline and extends from that.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, readAt, ct))
            return AssemblyVoteActionResult.WrongState;

        await audit.LogAsync(
            AuditAction.AssemblyVoteExtended, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Extended assembly vote {vote.Id} from {previous} to {newClosesAt}.",
            adminUserId);

        return AssemblyVoteActionResult.Ok;
    }

    public async Task<AssemblyVoteActionResult> CancelAsync(
        Guid voteId, string reason, Guid adminUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AssemblyVoteActionResult.Invalid;
        // Its own column is varchar(4000); over that the update dies on the insert, which is
        // a 500 where "that cancellation was rejected" is the honest answer.
        if (!Fits(reason, MaxCancelReasonLength)) return AssemblyVoteActionResult.Invalid;

        // Settle first: a vote whose deadline has passed is Closed with its result stored,
        // and cancelling it then would erase that result and the decision it recorded.
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        var now = clock.GetCurrentInstant();
        vote.Status = AssemblyVoteStatus.Cancelled;
        vote.ClosedAt = now;
        vote.ClosedByUserId = adminUserId;
        vote.CancelReason = reason;
        vote.UpdatedAt = now;

        // No result is computed: a cancelled vote decided nothing, and the ballots stay only
        // as a record that it was attempted. A refused write means the vote closed first,
        // and a closed vote is never cancelled out of its result.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, ct: ct))
            return AssemblyVoteActionResult.WrongState;

        await ClearOpenNotificationAsync(vote, adminUserId, ct);

        await audit.LogAsync(
            AuditAction.AssemblyVoteCancelled, AuditEntityTypes.AssemblyVote, vote.Id,
            Truncate($"Cancelled assembly vote {vote.Id}: {reason}", MaxAuditDescriptionLength),
            adminUserId);

        await AfterTransitionAsync(
            "notifying the roster that the vote was cancelled", vote.Id,
            token => NotifyRosterCancelledAsync(vote, token));
        return AssemblyVoteActionResult.Ok;
    }

    public async Task<(AssemblyVoteResult? Result, bool Recorded)> PeekAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return (null, false);

        // A peek at a closed vote is just the results page; the peek log exists to record
        // looking *early*, so only an open vote writes one. Recorded = false says so, because
        // a page that claims "this peek has been recorded" when nothing was is a worse lie
        // than no page at all. Note the vote can lapse inside SettleAsync, so the caller
        // cannot decide this for itself from an earlier read.
        if (vote.Status != AssemblyVoteStatus.Open)
        {
            return (vote.ResultJson is null
                ? null
                : JsonSerializer.Deserialize<AssemblyVoteResult>(vote.ResultJson, ResultJsonOptions),
                false);
        }

        var now = clock.GetCurrentInstant();

        // The peek row and the audit entry are written before the tally is handed back, so
        // there is no way to look without leaving the trace that says you did. The row goes in
        // only while the vote is still Open: the peek log is the record of looking *early*,
        // and a close that commits in this gap turns this request into an ordinary results
        // read, which is published on that page and must not accuse the Admin of an early look.
        var recorded = await repository.AddPeekAsync(
            new AssemblyVotePeek
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                AdminUserId = adminUserId,
                PeekedAt = now
            }, ct);

        if (!recorded)
        {
            var closed = await repository.GetByIdAsync(vote.Id, ct);
            return (closed?.ResultJson is { } stored
                ? JsonSerializer.Deserialize<AssemblyVoteResult>(stored, ResultJsonOptions)
                : null,
                false);
        }

        await audit.LogAsync(
            AuditAction.AssemblyVotePeeked, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Peeked at the live tally of open assembly vote {vote.Id}.", adminUserId);

        return (await ComputeResultAsync(vote, ct), true);
    }

    public async Task<IReadOnlyList<AssemblyBallotDisclosureRow>?> GetBallotsForBoardAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return null;
        // Closed, not merely terminal. A cancelled vote's ballots are retained as an
        // abandoned record with no result; disclosing who voted what on a vote that was
        // called off is not something the Board was ever granted.
        if (vote.Status != AssemblyVoteStatus.Closed) return null;

        await audit.LogAsync(
            AuditAction.AssemblyBallotsViewed, AuditEntityTypes.AssemblyVote, voteId,
            $"Listed the individual ballots of assembly vote {voteId}.", actorUserId);

        return await DisclosureRowsAsync(voteId, ct);
    }

    // ==========================================================================
    // Notifying the roster
    // ==========================================================================

    /// <summary>
    /// Raises one in-app notification keyed to the vote, which closing later resolves, and
    /// emails everyone on the roster that the vote is open.
    /// </summary>
    /// <remarks>
    /// Per-recipient try/catch, as in Surveys' invite send: one bad address must not leave
    /// the rest of the electorate uninformed about a vote that is already open.
    /// </remarks>
    private async Task NotifyRosterOpenedAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct)
    {
        // The notification goes out before the emails, not after. A stop or cancel that
        // commits mid-send resolves this vote's notification by source key, and a row that
        // does not exist yet cannot be resolved — so emitting after a roster-long email loop
        // leaves an actionable "vote is open" alert on a vote that is already closed, with
        // nothing left to clear it. Emitted first, the window is one round trip, and the
        // status re-read closes what is left of it.
        var userIds = roster.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList();
        if (userIds.Count > 0
            && (await repository.GetByIdAsync(vote.Id, ct))?.Status == AssemblyVoteStatus.Open)
        {
            await notifications.SendAsync(
                NotificationSource.AssemblyVoteOpened,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                NotificationTitle(vote),
                userIds,
                actionUrl: VoteUrl(vote.Id),
                sourceKey: vote.Id.ToString(),
                cancellationToken: ct);
        }

        await SendOpenedEmailsAsync(vote, roster, ct);
    }

    /// <summary>
    /// Emails the given roster rows that the vote is open, stamping each row that was sent.
    /// An unstamped row is one the email never reached, and the hourly sweep retries it.
    /// Returns how many rows the email actually reached.
    /// </summary>
    private async Task<int> SendOpenedEmailsAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> rows, CancellationToken ct)
    {
        var recipients = await RecipientsAsync(rows, ct);
        var notified = 0;
        var closesAt = ClosingLocal(vote.ClosesAt);

        foreach (var (rosterRow, info, address) in recipients)
        {
            try
            {
                await email.SendAsync(
                    messages.AssemblyVoteOpened(
                        address,
                        info.BurnerName,
                        EmailTitle(vote, info.PreferredLanguage),
                        closesAt,
                        rosterRow.IsOfficial,
                        VoteUrl(vote.Id),
                        info.PreferredLanguage),
                    ct);

                // Stamped per row, immediately, as the reminder path does: the stamp is what
                // tells the retry sweep this row was reached, so a batch that dies halfway —
                // or a stamp write that fails after the enqueues — must not leave every
                // delivered row looking unsent and earn the whole roster a duplicate notice.
                await repository.StampNotifiedAsync([rosterRow.Id], clock.GetCurrentInstant(), ct);
                notified++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to send the vote-opened email for assembly vote {VoteId} to roster row {RosterId}.",
                    vote.Id, rosterRow.Id);
            }
        }

        return notified;
    }

    /// <summary>
    /// The vote's title, trimmed to what <c>Notification.Title</c> holds (varchar(200)).
    /// The title is jsonb and uncapped, so a long motion title would otherwise throw on
    /// insert — after the vote is already irreversibly Open.
    /// </summary>
    private static string NotificationTitle(AssemblyVote vote) =>
        Truncate(vote.Title.Resolve(vote.OfficialCulture, vote.OfficialCulture), 200);

    /// <summary>
    /// The vote's title in the reader's language, bounded for the email subject that carries
    /// it (varchar(1000)). Drafts are bounded at <see cref="MaxTitleLength"/>, so this only
    /// bites on one written before that check existed — where the alternative is an enqueue
    /// that throws for every recipient after the vote is already Open.
    /// </summary>
    private static string EmailTitle(AssemblyVote vote, string language) =>
        Truncate(vote.Title.Resolve(language, vote.OfficialCulture), MaxTitleLength);

    /// <summary>Trims to <paramref name="max"/> characters, ellipsis included in the count.</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "\u2026");

    private async Task NotifyRosterCancelledAsync(AssemblyVote vote, CancellationToken ct)
    {
        var roster = await repository.GetRosterAsync(vote.Id, ct);
        var recipients = await RecipientsAsync(roster, ct);

        foreach (var (rosterRow, info, address) in recipients)
        {
            try
            {
                await email.SendAsync(
                    messages.AssemblyVoteCancelled(
                        address,
                        info.BurnerName,
                        EmailTitle(vote, info.PreferredLanguage),
                        vote.CancelReason ?? string.Empty,
                        info.PreferredLanguage),
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
    /// Pairs roster rows with their member's details and notification address, dropping
    /// anonymized rows and anyone with no reachable address.
    /// </summary>
    private async Task<List<(AssemblyVoteRoster Roster, UserInfo Info, string Address)>> RecipientsAsync(
        IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct)
    {
        var userIds = roster.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList();
        if (userIds.Count == 0) return [];

        var infos = await users.GetUserInfosAsync(userIds, ct);
        var addresses = await userEmails.GetNotificationTargetEmailsAsync(userIds, ct);

        return roster
            .Where(r => r.UserId is not null)
            .Select(r => (Roster: r, UserId: r.UserId!.Value))
            .Where(x => infos.ContainsKey(x.UserId) && addresses.ContainsKey(x.UserId))
            .Select(x => (x.Roster, infos[x.UserId], addresses[x.UserId]))
            .ToList();
    }

    private static string VoteUrl(Guid voteId) =>
        $"/Governance/Votes/{voteId}";

    /// <summary>
    /// The closing time in the association's own timezone, still unformatted: Email renders it
    /// under each recipient's culture, so the zone is decided here and the wording there.
    /// </summary>
    private static LocalDateTime ClosingLocal(Instant closesAt) =>
        closesAt.InZone(MadridZone).LocalDateTime;

    /// <summary>
    /// Closing times are announced to the electorate in the association's own timezone; a
    /// UTC instant in an email would be a different deadline to every reader.
    /// </summary>
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    // ==========================================================================
    // The hourly sweep
    // ==========================================================================

    public async Task<int> RunLapseAndReminderSweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        // Reminders first: closing a lapsed vote makes its roster ineligible for one.
        await SendRemindersAsync(now, ct);

        var lapsed = await repository.GetLapsedOpenVotesAsync(now, ct);
        var closed = 0;
        foreach (var vote in lapsed)
        {
            // Counted only when the close landed. A vote an Extend rescued between the query
            // and the write is still open, and a job that reports it as closed is wrong in
            // the one place anybody looks to see what the automation did.
            if (await CloseAsync(vote, closedByUserId: null, AuditAction.AssemblyVoteClosed,
                    $"Assembly vote {vote.Id} closed automatically at its announced time.", ct))
            {
                closed++;
            }
        }

        if (closed > 0)
        {
            logger.LogInformation("Closed {Count} lapsed assembly vote(s).", closed);
        }

        // A close interrupted after its status write leaves a vote that is Closed with no
        // result, which no lapse query finds — it is not Open any more. The read paths finish
        // one when somebody looks; this is so nobody has to. The whole table fits in memory.
        foreach (var vote in await repository.GetAllAsync(ct))
        {
            await FinishInterruptedCloseAsync(vote, ct);
            await RetryOpenedEmailsAsync(vote, ct);
        }

        return closed;
    }

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
    private async Task RetryOpenedEmailsAsync(AssemblyVote vote, CancellationToken ct)
    {
        if (vote.Status != AssemblyVoteStatus.Open) return;

        var unsent = (await repository.GetRosterAsync(vote.Id, ct))
            .Where(r => r.NotifiedAt is null)
            .ToList();
        if (unsent.Count == 0) return;

        var sent = await SendOpenedEmailsAsync(vote, unsent, ct);
        if (sent == 0) return;

        // Automation that emails the electorate says so under the job actor, as the reminder
        // batch does. The Admin's AssemblyVoteOpened entry is about their transition; it
        // cannot show that the job reached members hours later, which is exactly the thing
        // the Board would be looking for.
        await audit.LogAsync(
            AuditAction.AssemblyVoteOpened, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Re-sent the opening email for assembly vote {vote.Id} to {sent} roster member(s) "
            + "the first send never reached.",
            LapseJobName);
    }

    /// <summary>
    /// Sends the T-24h reminder to roster members who have not voted, stamping each row so
    /// the reminder cannot repeat however often the job runs, and auditing the batch that
    /// was delivered under the job actor.
    /// </summary>
    private async Task SendRemindersAsync(Instant now, CancellationToken ct)
    {
        var due = await repository.GetOpenVotesClosingBetweenAsync(now, now + ReminderLeadTime, ct);

        foreach (var vote in due)
        {
            var pending = await repository.GetRosterNeedingReminderAsync(vote.Id, ct);
            if (pending.Count == 0) continue;

            var recipients = await RecipientsAsync(pending, ct);
            var reminded = new List<Guid>(pending.Count);

            foreach (var (rosterRow, info, address) in recipients)
            {
                // And the clock per recipient too: a vote whose deadline passes mid-batch is
                // still persisted Open until the close pass below runs, so comparing against
                // the sweep-start instant would keep mailing "closing soon" after it closed.
                var sentAt = clock.GetCurrentInstant();

                // Eligibility is re-read per recipient, not trusted from the list: sending
                // the whole roster takes long enough for somebody to vote, or for an Admin to
                // stop, cancel or extend the vote, and "you have not voted and the vote closes
                // soon" is wrong in each case — all but the first for everybody left.
                var current = await repository.GetByIdAsync(vote.Id, ct);
                if (current is null || current.Status != AssemblyVoteStatus.Open) break;

                // The deadline comes from that same read, not from the queried row. An Extend
                // mid-batch moves it, and half an electorate holding an email that announces a
                // deadline the vote no longer has is worse than no reminder: the rest of the
                // roster is reminded on the sweep after the new deadline comes into range.
                if (current.ClosesAt <= sentAt || current.ClosesAt > sentAt + ReminderLeadTime) break;
                var closesAt = ClosingLocal(current.ClosesAt);

                if (await repository.GetBallotForRosterAsync(vote.Id, rosterRow.Id, ct) is not null)
                {
                    continue;
                }

                try
                {
                    await email.SendAsync(
                        messages.AssemblyVoteReminder(
                            address,
                            info.BurnerName,
                            EmailTitle(current, info.PreferredLanguage),
                            closesAt,
                            rosterRow.IsOfficial,
                            VoteUrl(vote.Id),
                            info.PreferredLanguage),
                        ct);

                    // Stamped per row, immediately: the stamp is the only thing stopping the
                    // next sweep re-sending, so the widest window worth having between the
                    // send and the stamp is one roster row, not the whole batch.
                    await repository.StampReminderSentAsync([rosterRow.Id], sentAt, ct);
                    reminded.Add(rosterRow.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to send the vote reminder for assembly vote {VoteId} to roster row {RosterId}.",
                        vote.Id, rosterRow.Id);
                }
            }

            if (reminded.Count > 0)
            {
                // Automation that emails the electorate says so in the audit log (US-V8).
                // The count is the one actually delivered, not the one attempted: a send
                // that threw above is in the log as an error and is not stamped, so the
                // reminder is retried on the next sweep.
                await audit.LogAsync(
                    AuditAction.AssemblyVoteRemindersSent, AuditEntityTypes.AssemblyVote, vote.Id,
                    $"Sent the closing reminder for assembly vote {vote.Id} to {reminded.Count} roster member(s).",
                    LapseJobName);
            }
        }
    }

    // ==========================================================================
    // GDPR and account merge
    // ==========================================================================

    /// <summary>
    /// The member's own voting record: which votes they were entitled to vote in, whether
    /// their ballot was official, and every revision of what they recorded.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var record = await repository.GetVotingRecordForUserAsync(userId, ct);

        var rows = record
            .Select(x => new
            {
                Vote = x.Vote.Title.Resolve(x.Vote.OfficialCulture, x.Vote.OfficialCulture),
                x.Vote.Status,
                ClosesAt = x.Vote.ClosesAt.ToIso8601(),
                Entitlement = x.Roster.IsOfficial ? "Official" : "Indicative",
                x.Roster.Tier,
                x.Roster.IsBoardMember,
                Ballot = x.Ballot is null
                    ? null
                    : new
                    {
                        x.Ballot.Choice,
                        x.Ballot.Ranking,
                        x.Ballot.Revision,
                        CastAt = x.Ballot.CastAt.ToIso8601(),
                        UpdatedAt = x.Ballot.UpdatedAt.ToIso8601(),
                        History = x.Ballot.History
                            .OrderBy(h => h.Revision)
                            .Select(h => new
                            {
                                h.Revision,
                                h.Choice,
                                h.Ranking,
                                RecordedAt = h.RecordedAt.ToIso8601()
                            })
                            .ToList()
                    }
            })
            .ToList();

        var (acted, peeks) = await repository.GetActorRecordForUserAsync(userId, ct);

        var actions = acted
            .Select(v => new
            {
                Vote = v.Title.Resolve(v.OfficialCulture, v.OfficialCulture),
                v.Status,
                Roles = new[]
                    {
                        v.CreatedByUserId == userId ? "Drafted" : null,
                        v.OpenedByUserId == userId ? "Opened" : null,
                        v.ClosedByUserId == userId ? "Closed" : null
                    }
                    .OfType<string>()
                    .ToList(),
                CreatedAt = v.CreatedAt.ToIso8601(),
                OpenedAt = v.OpenedAt.ToIso8601(),
                ClosedAt = v.ClosedAt.ToIso8601()
            })
            .ToList();

        var peekRows = peeks
            .Select(x => new
            {
                Vote = x.Vote.Title.Resolve(x.Vote.OfficialCulture, x.Vote.OfficialCulture),
                PeekedAt = x.Peek.PeekedAt.ToIso8601()
            })
            .ToList();

        return
        [
            new UserDataSlice(GdprExportSections.AssemblyVotes, rows),
            new UserDataSlice(GdprExportSections.AssemblyVoteActions,
                new { RanVotes = actions, Peeks = peekRows })
        ];
    }

    /// <summary>
    /// Art. 17 erasure is partial here, and deliberately so: a vote is the association's
    /// record of a decision it took, so the ballot survives while the person is unlinked
    /// from it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.AssemblyVotes] =
                "Partially retained: the member's roster row is anonymized (the link to the "
                + "account is removed) but the row and its ballot are kept, because the vote "
                + "is the association's legal record of an agreement it adopted and the "
                + "turnout and stored result must stay arithmetically valid "
                + "(Ley Organica 1/2002 Art. 14; GDPR Art. 17(3)(b) and (e)). After erasure "
                + "the ballot can no longer be attributed to the person.",

            [GdprExportSections.AssemblyVoteActions] =
                "Retained: who drafted, opened or closed a vote, and who looked at a live "
                + "tally before it closed, are part of the association's record of how the "
                + "decision was taken — the acta names the closer and the results page "
                + "publishes the early-view list (GDPR Art. 17(3)(b) and (e))."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var anonymized = await repository.AnonymizeRosterForUserAsync(userId, ct);
        if (anonymized > 0)
        {
            logger.LogInformation(
                "Anonymized {Count} assembly-vote roster row(s) for the erased user.", anonymized);
        }
    }

    /// <summary>
    /// Folds a merged account's entitlements into the surviving one. Where both accounts sat
    /// on the same vote's roster the target's row wins — one person may hold only one ballot
    /// per vote — and the dropped row's ballot moves to the survivor, or is destroyed when
    /// the survivor voted too. Either way it is audited: one of them destroys a recorded
    /// ballot and the other changes which roster row a recorded ballot counts under.
    /// </summary>
    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var dropped = await repository.ReassignRosterToUserAsync(mergedFromUserId, mergedToUserId, ct);

        foreach (var drop in dropped)
        {
            // The ballot is the entity when there was one, the vote when the merged-from
            // account was on the roster but never voted. Logging the roster row's own id
            // under either discriminator would leave an audit entry that resolves to
            // nothing — and after a drop the row is gone anyway.
            var (entityType, entityId) = drop.BallotId is { } ballotId
                ? (AuditEntityTypes.AssemblyBallot, ballotId)
                : (AuditEntityTypes.AssemblyVote, drop.VoteId);

            var what = drop.BallotMoved
                ? "merged-from account's roster row was dropped in favour of the surviving "
                    + "account's, and its ballot moved onto that row — the surviving account "
                    + "had not voted, so this is the same human's only ballot on the vote."
                : "merged-from account's roster row was dropped in favour of the surviving "
                    + "account's, and its ballot with it: both accounts had voted, and one "
                    + "person may hold only one ballot per vote.";

            await audit.LogAsync(
                AuditAction.AssemblyVoteRosterMerged, entityType, entityId,
                "Account merge: both accounts were on the same assembly-vote roster, so the "
                + what,
                actorUserId, drop.VoteId, AuditEntityTypes.AssemblyVote);
        }
    }
}
