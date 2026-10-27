using Humans.Governance.Contracts;
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
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteService(
    IAssemblyVoteRepository repository,
    IApplicationRepository applications,
    IRoleAssignmentService roleAssignments,
    ITeamService teams,
    IUserService users,
    IUserEmailService userEmails,
    IEmailService email,
    GovernanceEmails messages,
    INotificationEmitter notifications,
    INotificationAutoResolve notificationResolve,
    IAuditLogService audit,
    IGoogleTranslationService translation,
    IClock clock,
    ILogger<AssemblyVoteService> logger)
    : IAssemblyVoteService, IUserDataContributor, IUserMerge
{
    internal const string AssemblyVotes = "AssemblyVotes";
    internal const string AssemblyVoteActions = "AssemblyVoteActions";

    /// <summary>The recurring job that closes lapsed votes and sends the T-24h reminder.</summary>
    internal const string LapseJobName = "governance-assembly-vote-lapse";

    // Bounded text columns, mirrored from the EF configurations: form text is checked with Fits
    // so an over-long value is a rejected draft rather than a 500. Title's jsonb column is
    // uncapped, but it is copied into bounded email and notification columns after an
    // irreversible transition, so it is bounded at the draft while the vote is still editable.
    internal const int MaxTitleLength = 200;         // assembly_votes.Title, per culture
    internal const int MaxOptionKeyLength = 100;     // assembly_vote_options.Key
    internal const int MaxCancelReasonLength = 4000; // assembly_votes.CancelReason
    internal const int MaxInfoUrlLength = 2000;      // assembly_votes.InfoUrl
    internal const int MaxCultureLength = 10;        // assembly_votes.OfficialCulture

    /// <summary>Whether a value fits the column that will store it. Blank always fits.</summary>
    private static bool Fits(string? value, int max) => (value?.Length ?? 0) <= max;

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
    /// Enums as names so a stored result survives CLR reordering; NodaTime because
    /// <see cref="AssemblyVoteResult.ComputedAt"/> would otherwise read back as <c>Instant.MinValue</c>.
    /// </summary>
    private static readonly JsonSerializerOptions ResultJsonOptions =
        new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }
            .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    public async Task<IReadOnlyList<AssemblyVoteListItem>> GetVotesForMemberAsync(
        Guid userId, CancellationToken ct = default)
    {
        var votes = await SettleAllAsync(ct);
        var items = new List<AssemblyVoteListItem>(votes.Count);

        // Members never see a draft. Chain ids are resolved once for the whole list: a member
        // with no roster row of their own is the common case, and exactly the one that has to ask.
        var chainIds = (await users.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

        foreach (var vote in votes.Where(v => v.Status != AssemblyVoteStatus.Draft))
        {
            var roster = await EffectiveRosterAsync(vote.Id, userId, ct, chainIds);
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

        var roster = await EffectiveRosterAsync(voteId, userId, ct);
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
            // Only when the viewer's culture was actually authored: Resolve falls back to the
            // official text, and labelling the binding text "a translation" would be wrong.
            IsTranslation: vote.OfficialText.HasCulture(culture)
                           && !culture.Equals(vote.OfficialCulture, StringComparison.OrdinalIgnoreCase),
            SafeInfoUrl(vote.InfoUrl),
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
        var roster = await EffectiveRosterAsync(voteId, userId, ct);
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

        // First cast vs change comes from the revision the locked upsert assigned: two first
        // ballots can both read none. Neither entry records the choice, and the entity is the vote,
        // never the ballot — audit rows outlive erasure, so a ballot id would re-link the person.
        await audit.LogAsync(
            ballot.Revision == 1 ? AuditAction.AssemblyBallotCast : AuditAction.AssemblyBallotChanged,
            AuditEntityTypes.AssemblyVote,
            voteId,
            ballot.Revision == 1
                ? $"Cast a ballot on assembly vote {voteId} (revision {ballot.Revision})."
                : $"Changed their ballot on assembly vote {voteId} (revision {ballot.Revision}).",
            userId);

        await AfterTransitionAsync(
            "resolving the voter's open-vote notification", voteId,
            token => notificationResolve.ResolveBySourceAsync(
                userId, NotificationSource.AssemblyVoteOpened, token, sourceKey: voteId.ToString()));

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

        // Refused: an Extend moved the deadline or another close landed first. CloseAsync already
        // stamped this object Closed, so the row, not this object, is the truth.
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

        // A close that committed its status seconds ago is still running its own tail; only one
        // that has stood past the grace period is taken over, or the audit entry would duplicate.
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
        // A lapsed vote closed at its announced time, whenever it was noticed; BuildActa prints
        // ClosedAt into the legal summary. An Admin Stop really does close it now.
        vote.ClosedAt = closedByUserId is null ? vote.ClosesAt : now;
        vote.ClosedByUserId = closedByUserId;
        vote.UpdatedAt = now;

        // The status is persisted *before* the ballots are read: from that commit on,
        // UpsertBallotAsync refuses, so every accepted ballot is in the table when counting looks.
        // Refused means another actor closed or extended first and owns the whole tail.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, ct: ct)) return false;

        await FinishCloseAsync(vote, closedByUserId, action, description, ct);
        return true;
    }

    /// <summary>
    /// Everything a close owes after its status is down; <see cref="SettleAsync"/> replays it. The
    /// stored result is written last because its absence is what the replay looks for: the worst a
    /// crash costs is a duplicate close entry, never a missing one.
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

    /// <summary>Stores the tally on the already-closed vote; also the settle path's repair.</summary>
    private async Task StoreResultAsync(AssemblyVote vote, CancellationToken ct)
    {
        var result = await ComputeResultAsync(vote, ct);
        vote.ResultJson = JsonSerializer.Serialize(result, ResultJsonOptions);

        // Closed is the state this write expects and the one it leaves: the tally lands on the
        // row the close already stamped, and a vote cancelled out from under it gets nothing.
        await repository.UpdateAsync(vote, AssemblyVoteStatus.Closed, ct: ct);
    }

    /// <summary>
    /// Best-effort: every caller has already persisted a terminal status with no retry, so a throw
    /// here would strand the audit and email work still owed. Logged and swallowed.
    /// </summary>
    private Task ClearOpenNotificationAsync(
        AssemblyVote vote, Guid? actorUserId, CancellationToken _) =>
        AfterTransitionAsync(
            "resolving the open-vote notification", vote.Id,
            ct => notificationResolve.ResolveBySourceKeyAsync(
                NotificationSource.AssemblyVoteOpened, vote.Id.ToString(), actorUserId, ct));

    /// <summary>
    /// Runs a side effect after an irreversible, committed transition without letting a throw
    /// leave the audit entry, roster email or notification permanently missing.
    /// </summary>
    private async Task AfterTransitionAsync(
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

        var roster = await EffectiveRosterAsync(voteId, userId, ct);
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

    /// <summary>
    /// The acta block: a plain-text summary the Secretary pastes into the minutes. Deliberately
    /// unlocalized and unstyled — it goes into a Spanish legal document, and the numbers are
    /// what statutes Art. 8.6 requires.
    /// </summary>
    private static string BuildActa(
        AssemblyVote vote, AssemblyVoteResult result, string? closedByName)
    {
        var acta = new StringBuilder();
        var official = result.Official;

        // The counting result is keyed by option key; the acta is read by people, in the vote's
        // official culture like the title above it.
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
        // An out-of-range numeric enum binds silently and falls through to the wrong default — an
        // undefined Kind opens with no options yet counts as instant-runoff. Content locks at open.
        if (!Enum.IsDefined(draft.Kind)) return false;
        if (!Enum.IsDefined(draft.RequiredMajority)) return false;
        if (!Enum.IsDefined(draft.IndicativeAudience)) return false;
        if (!Enum.IsDefined(draft.BallotDisclosure)) return false;
        if (string.IsNullOrWhiteSpace(draft.OfficialCulture)) return false;
        if (!Fits(draft.OfficialCulture, MaxCultureLength)) return false;
        if (!Fits(draft.InfoUrl, MaxInfoUrlLength)) return false;
        if (!string.IsNullOrWhiteSpace(draft.InfoUrl) && SafeInfoUrl(draft.InfoUrl) is null) return false;
        if (!draft.Title.TryGetValue(draft.OfficialCulture, out var title)
            || string.IsNullOrWhiteSpace(title)) return false;
        if (draft.Title.Values.Any(t => !Fits(t, MaxTitleLength))) return false;
        if (!draft.OfficialText.TryGetValue(draft.OfficialCulture, out var text)
            || string.IsNullOrWhiteSpace(text)) return false;
        if (draft.ClosesAt <= clock.GetCurrentInstant()) return false;

        if (draft.Kind != AssemblyVoteKind.RankedChoice) return true;

        // A 2/3 threshold has no defined meaning under instant runoff, and the acta would claim a
        // threshold the count never applied. Qualified majorities are asked as YesNo questions.
        if (draft.RequiredMajority != RequiredMajority.Simple) return false;

        return draft.Options.Count >= 2
               && draft.Options.All(o => !string.IsNullOrWhiteSpace(o.Key)
                                         && Fits(o.Key, MaxOptionKeyLength))
               && draft.Options.All(o => o.Label.TryGetValue(draft.OfficialCulture, out var label)
                                         && !string.IsNullOrWhiteSpace(label))
               && draft.Options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count()
                  == draft.Options.Count;
    }

    // Used on reads too: older rows may predate URL validation.
    private static string? SafeInfoUrl(string? url)
    {
        var trimmed = url?.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" ? trimmed : null;
    }

    private static void ApplyDraft(AssemblyVote vote, AssemblyVoteDraft draft, Instant now)
    {
        vote.Title = new GovernanceLocalizedText(draft.Title.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        vote.OfficialText = new GovernanceLocalizedText(
            draft.OfficialText.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        vote.OfficialCulture = draft.OfficialCulture;
        vote.InfoUrl = SafeInfoUrl(draft.InfoUrl);
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
        await AfterTransitionAsync(
            "auditing the vote opening", vote.Id,
            _ => audit.LogAsync(
                AuditAction.AssemblyVoteOpened, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Opened assembly vote {vote.Id}: {officialCount} official and "
                + $"{roster.Count - officialCount} indicative roster members.",
                adminUserId));

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
        // The revision this extension was built from: two concurrent Extends must not let the
        // shorter commit second and move the electorate's deadline backwards.
        var readAt = vote.UpdatedAt;
        var extendedAt = clock.GetCurrentInstant();
        vote.ClosesAt = newClosesAt;
        vote.UpdatedAt = extendedAt;

        // The vote closed, or was extended by somebody else, between the read above and this
        // write. Nothing was extended, and "only an open vote can be extended" is the honest
        // answer: the admin re-reads the new deadline and extends from that.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, readAt, ct))
            return AssemblyVoteActionResult.WrongState;

        await AfterTransitionAsync(
            "auditing the extension", vote.Id,
            _ => audit.LogAsync(
                AuditAction.AssemblyVoteExtended, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Extended assembly vote {vote.Id} from {previous} to {newClosesAt}.",
                adminUserId));

        // Re-arm the T-24h reminder: stamps up to now mean "told about the old deadline"; later
        // stamps come from a sweep already announcing the new one. Post-transition, not on the
        // request token: there is no replay for an extension.
        await AfterTransitionAsync(
            "re-arming the reminder after the extension", vote.Id,
            token => repository.ClearReminderStampsAsync(vote.Id, extendedAt, token));

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

        await AfterTransitionAsync(
            "auditing the vote cancellation", vote.Id,
            _ => audit.LogAsync(
                AuditAction.AssemblyVoteCancelled, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Cancelled assembly vote {vote.Id}: {reason}",
                adminUserId));

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

        // The peek log records looking *early*, so only an open vote writes one, and Recorded =
        // false says so. The vote can lapse inside SettleAsync, so the caller cannot decide this.
        if (vote.Status != AssemblyVoteStatus.Open)
        {
            return (vote.ResultJson is null
                ? null
                : JsonSerializer.Deserialize<AssemblyVoteResult>(vote.ResultJson, ResultJsonOptions),
                false);
        }

        var now = clock.GetCurrentInstant();

        // The peek row and audit entry are written before the tally is handed back, so there is
        // no looking without a trace. A close in this gap makes it an ordinary results read.
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
            await notifications.SendAsync(
                NotificationSource.AssemblyVoteOpened,
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
    /// unstamped row is retried by the hourly sweep. Returns how many members were reached.
    /// Takes the whole roster so a human with a stamped row is not mailed again through another.
    /// </summary>
    private async Task<int> SendOpenedEmailsAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct)
    {
        var recipients = (await RecipientsAsync(roster, ct))
            .Where(r => r.Rows.All(row => row.NotifiedAt is null))
            .ToList();
        var notified = 0;

        foreach (var (rosterRow, rows, info, address, language) in recipients)
        {
            // Re-read per recipient, with the clock: a stop, cancel, extend or lapse mid-batch
            // ends it rather than mailing the rest a deadline — or a vote — that no longer stands.
            var sentAt = clock.GetCurrentInstant();
            var current = await repository.GetByIdAsync(vote.Id, ct);
            if (current is null || !current.AcceptsBallotsAt(sentAt)) break;
            var closesAt = ClosingLocal(current.ClosesAt);

            try
            {
                await email.SendAsync(
                    messages.AssemblyVoteOpened(
                        address,
                        info.BurnerName,
                        EmailTitle(current, language),
                        closesAt,
                        rosterRow.IsOfficial,
                        VoteUrl(vote.Id),
                        language),
                    ct);

                // Stamped per row immediately, so a batch dying halfway does not earn the whole
                // roster a duplicate; conditional on the deadline this email named.
                await repository.StampNotifiedAsync(
                    rows.Select(r => r.Id).ToList(), clock.GetCurrentInstant(), current.ClosesAt, ct);
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
    /// Bounded for the email subject (varchar(1000)): only bites on a draft written before
    /// <see cref="MaxTitleLength"/> existed, where it would otherwise throw after the vote opened.
    /// </summary>
    private static string EmailTitle(AssemblyVote vote, string language) =>
        Truncate(vote.Title.Resolve(language, vote.OfficialCulture), MaxTitleLength);

    /// <summary>Trims to <paramref name="max"/> characters, ellipsis included in the count.</summary>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var length = max - 1;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return string.Concat(text.AsSpan(0, length), "\u2026");
    }

    private async Task NotifyRosterCancelledAsync(AssemblyVote vote, CancellationToken ct)
    {
        var roster = await repository.GetRosterAsync(vote.Id, ct);
        var recipients = await RecipientsAsync(roster, ct);

        foreach (var (rosterRow, _, info, address, language) in recipients)
        {
            try
            {
                await email.SendAsync(
                    messages.AssemblyVoteCancelled(
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
    /// The roster row entitling this human: their own, or one left on an account merged into
    /// theirs. A row already carrying a ballot wins, so a human enrolled twice amends their ballot
    /// rather than casting a second; two ballot-bearing rows resolve to the first by id.
    /// </summary>
    private async Task<AssemblyVoteRoster?> EffectiveRosterAsync(
        Guid voteId, Guid userId, CancellationToken ct, IReadOnlyList<Guid>? chainIds = null)
    {
        // Every id the human has held: the resolved record's own id first and the archived
        // ids after it in id order, not set enumeration order, which comes out of a cache
        // and is not stable, and which row a member is shown must not vary between reads.
        chainIds ??= (await users.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        if (chainIds.Count == 1) return await repository.GetRosterRowAsync(voteId, chainIds[0], ct);

        var candidates = new List<AssemblyVoteRoster>(chainIds.Count);
        foreach (var id in chainIds)
        {
            if (await repository.GetRosterRowAsync(voteId, id, ct) is { } row)
            {
                candidates.Add(row);
            }
        }

        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        foreach (var candidate in candidates)
        {
            if (await repository.GetBallotForRosterAsync(voteId, candidate.Id, ct) is not null)
            {
                return candidate;
            }
        }

        return candidates[0];
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
        var addresses = await userEmails.GetNotificationTargetEmailsAsync(userIds, ct);

        return roster
            .Where(r => r.UserId is not null
                && infos.ContainsKey(r.UserId.Value) && addresses.ContainsKey(r.UserId.Value))
            .GroupBy(r => infos[r.UserId!.Value].Id)
            .Select(g =>
            {
                var representative = g.FirstOrDefault(r => r.IsOfficial) ?? g.First();
                var info = infos[representative.UserId!.Value];
                return (
                    representative,
                    (IReadOnlyList<AssemblyVoteRoster>)g.ToList(),
                    info,
                    addresses[representative.UserId!.Value],
                    info.PreferredLanguage.IsSupportedCultureCode()
                        ? info.PreferredLanguage : CultureCatalog.DefaultCultureCode);
            })
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

        var roster = await repository.GetRosterAsync(vote.Id, ct);
        if (roster.All(r => r.NotifiedAt is not null)) return;

        // The whole roster, not the unstamped rows: a human holding a stamped row and an
        // unstamped one was reached, and the send decides that over all their rows.
        var sent = await SendOpenedEmailsAsync(vote, roster, ct);
        if (sent == 0) return;

        // Automation that emails the electorate says so under the job actor: the Admin's
        // AssemblyVoteOpened entry cannot show the job reached members hours later.
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

            // Grouped over the whole roster: a human with one voted or reminded row and one blank
            // row is left alone, or a member who voted is told they have not.
            var pendingIds = pending.Select(r => r.Id).ToHashSet();
            var recipients = (await RecipientsAsync(await repository.GetRosterAsync(vote.Id, ct), ct))
                .Where(r => r.Rows.All(row => pendingIds.Contains(row.Id)))
                .ToList();
            var reminded = new List<Guid>(recipients.Count);

            foreach (var (rosterRow, rows, info, address, language) in recipients)
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
                var closesAt = ClosingLocal(current.ClosesAt);

                // Over every row the human holds: a ballot cast mid-batch lands on whichever
                // of their rows EffectiveRosterAsync resolved them to.
                if (await HasBallotOnAnyRowAsync(vote.Id, rows, ct))
                {
                    continue;
                }

                try
                {
                    await email.SendAsync(
                        messages.AssemblyVoteReminder(
                            address,
                            info.BurnerName,
                            EmailTitle(current, language),
                            closesAt,
                            rosterRow.IsOfficial,
                            VoteUrl(vote.Id),
                            language),
                        ct);

                    // Stamped per row immediately, conditional on the deadline this email named.
                    await repository.StampReminderSentAsync(
                        rows.Select(r => r.Id).ToList(), sentAt, current.ClosesAt, ct);
                    // One entry per human, not per row: every row of a merge chain is stamped,
                    // but the member behind them got one email and the audit count says members.
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
                // The count actually delivered: a send that threw is unstamped and retried.
                await audit.LogAsync(
                    AuditAction.AssemblyVoteRemindersSent, AuditEntityTypes.AssemblyVote, vote.Id,
                    $"Sent the closing reminder for assembly vote {vote.Id} to {reminded.Count} roster member(s).",
                    LapseJobName);
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

    /// <summary>
    /// The member's own voting record: which votes they were entitled to vote in, whether
    /// their ballot was official, and every revision of what they recorded.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);

        // Plus every account merged into this one: the merge leaves roster rows and ballots on
        // the merged-away id, and the erasure path follows the same chain.
        IReadOnlyList<(AssemblyVoteRoster Roster, AssemblyVote Vote, AssemblyBallot? Ballot)> record = [];
        foreach (var id in info?.AllUserIds ?? [userId])
        {
            record = [.. record, .. await repository.GetVotingRecordForUserAsync(id, ct)];
        }

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

        // The survivor's id alone, not AllUserIds — the mirror image of the roster read above.
        // ReassignAsync moves the actor columns onto the survivor at merge time, so a merged-away
        // id owns no actor rows and asking with the raw id returns an empty slice.
        var actorId = info?.Id ?? userId;
        var (acted, peeks) = await repository.GetActorRecordForUserAsync(actorId, ct);

        var actions = acted
            .Select(v => new
            {
                Vote = v.Title.Resolve(v.OfficialCulture, v.OfficialCulture),
                v.Status,
                Roles = new[]
                    {
                        v.CreatedByUserId == actorId ? "Drafted" : null,
                        v.OpenedByUserId == actorId ? "Opened" : null,
                        v.ClosedByUserId == actorId ? "Closed" : null
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
            new UserDataSlice(AssemblyVotes, rows),
            new UserDataSlice(AssemblyVoteActions,
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
            [AssemblyVotes] =
                "Partially retained: the member's roster row is anonymized (the link to the "
                + "account is removed) but the row and its ballot are kept, because the vote "
                + "is the association's legal record of an agreement it adopted and the "
                + "turnout and stored result must stay arithmetically valid "
                + "(Ley Organica 1/2002 Art. 14; GDPR Art. 17(3)(b) and (e)). After erasure "
                + "the ballot can no longer be attributed to the person.",

            [AssemblyVoteActions] =
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
    /// Folds a merged account into the surviving one — the section's own actor columns only.
    /// Roster rows and ballots stay exactly as they were cast: they record what happened at
    /// the vote, not who holds the account today, and a human enrolled or voting twice is a
    /// defect in the vote that the merge must not erase. Each such row is audited so the
    /// Board can see the merge found it and left it standing.
    /// </summary>
    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var rosteredVoteIds =
            await repository.ReassignVoteActorsToUserAsync(mergedFromUserId, mergedToUserId, ct);

        foreach (var voteId in rosteredVoteIds)
        {
            await audit.LogAsync(
                AuditAction.AssemblyVoteRosterMerged, AuditEntityTypes.AssemblyVote, voteId,
                $"Account merge: the merged-away account {mergedFromUserId} was on this "
                + "vote's roster, and its roster row and any ballot were left as cast. The "
                + "roster and the ballot record what happened at the vote, so if both "
                + "accounts voted, that double vote stays visible rather than being tidied "
                + "away. The merge chain resolves the surviving account to the merged-away "
                + "id.",
                actorUserId, voteId, AuditEntityTypes.AssemblyVote);
        }
    }
}
