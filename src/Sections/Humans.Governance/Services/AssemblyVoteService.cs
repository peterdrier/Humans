using Humans.Governance.Contracts;
using System.Globalization;
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
    private readonly AssemblyVoteRosterBuilder rosterBuilder = new(applications, roleAssignments, teams, users, clock);
    private readonly AssemblyVoteResultReader resultReader = new(repository, users, clock);
    private readonly AssemblyVoteDataExport dataExport = new(repository, users);
    private readonly AssemblyVoteDrafts drafts = new(repository, translation, clock, logger);

    private readonly AssemblyVoteEffects effects = new(
        repository, users, email, messages, notifications, notificationResolve, audit, clock, logger,
        MaxTitleLength, LapseJobName);

    internal const string AssemblyVotes = AssemblyVoteDataExport.AssemblyVotes;
    internal const string AssemblyVoteActions = AssemblyVoteDataExport.AssemblyVoteActions;

    /// <summary>The recurring job that closes lapsed votes and sends the T-24h reminder.</summary>
    internal const string LapseJobName = "governance-assembly-vote-lapse";

    // Bounded text columns, mirrored from the EF configurations: form text is checked with Fits
    // so an over-long value is a rejected draft rather than a 500. Title's jsonb column is
    // uncapped, but it is copied into bounded email and notification columns after an
    // irreversible transition, so it is bounded at the draft while the vote is still editable.
    internal const int MaxTitleLength = AssemblyVoteDrafts.MaxTitleLength;         // assembly_votes.Title, per culture
    internal const int MaxCancelReasonLength = 4000; // assembly_votes.CancelReason

    /// <summary>Whether a value fits the column that will store it. Blank always fits.</summary>
    private static bool Fits(string? value, int max) => AssemblyVoteDrafts.Fits(value, max);

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

        return items;
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
            ballot.Revision == 1 ? GovernanceAuditActions.AssemblyBallotCast : GovernanceAuditActions.AssemblyBallotChanged,
            AuditEntityTypes.AssemblyVote,
            voteId,
            ballot.Revision == 1
                ? $"Cast a ballot on assembly vote {voteId} (revision {ballot.Revision})."
                : $"Changed their ballot on assembly vote {voteId} (revision {ballot.Revision}).",
            userId);

        await effects.AfterTransitionAsync(
            "resolving the voter's open-vote notification", voteId,
            token => notificationResolve.ResolveBySourceAsync(
                userId, GovernanceNotificationSources.AssemblyVoteOpened, token, sourceKey: voteId.ToString()));

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

        if (await CloseAsync(vote, closedByUserId: null, GovernanceAuditActions.AssemblyVoteClosed,
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
            vote.ClosedByUserId is null ? GovernanceAuditActions.AssemblyVoteClosed : GovernanceAuditActions.AssemblyVoteStopped,
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
        AuditActionDefinition action,
        string description,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();

        vote.Status = AssemblyVoteStatus.Closed;
        // A lapsed vote closed at its announced time, whenever it was noticed; the acta prints
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
        AuditActionDefinition action,
        string description,
        CancellationToken ct)
    {
        await effects.ClearOpenNotificationAsync(vote, closedByUserId, ct);

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
        var result = await resultReader.ComputeResultAsync(vote, ct);
        vote.ResultJson = JsonSerializer.Serialize(result, ResultJsonOptions);

        // Closed is the state this write expects and the one it leaves: the tally lands on the
        // row the close already stamped, and a vote cancelled out from under it gets nothing.
        await repository.UpdateAsync(vote, AssemblyVoteStatus.Closed, ct: ct);
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

        var disclosed = disclose ? await resultReader.DisclosureRowsAsync(voteId, ct) : null;

        if (viewerIsBoardOrAdmin)
        {
            await audit.LogAsync(
                GovernanceAuditActions.AssemblyBallotsViewed, AuditEntityTypes.AssemblyVote, voteId,
                $"Viewed the individual ballots of assembly vote {voteId} on the results page.",
                userId);
        }

        return new AssemblyVoteResultsView(
            await DetailAsync(vote, roster, ownBallot, ct),
            result,
            await resultReader.PeekViewsAsync(voteId, ct),
            disclosed,
            AssemblyVotePresentation.BuildActa(vote, result, await resultReader.ClosedByNameAsync(vote, ct)));
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

        return items;
    }

    public Task<AssemblyVoteDraft?> GetDraftAsync(Guid voteId, CancellationToken ct = default) =>
        drafts.GetDraftAsync(voteId, ct);

    public Task<Guid?> CreateDraftAsync(AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default) =>
        drafts.CreateDraftAsync(draft, actorUserId, ct);

    public Task<AssemblyVoteActionResult> UpdateDraftAsync(
        Guid voteId, AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default) =>
        drafts.UpdateDraftAsync(voteId, draft, actorUserId, ct);

    public Task<int> PreFillTranslationsAsync(
        Guid voteId, IReadOnlyList<string> targetCultures, Guid actorUserId, CancellationToken ct = default) =>
        drafts.PreFillTranslationsAsync(voteId, targetCultures, actorUserId, ct);

    public Task<AssemblyVoteActionResult> DeleteDraftAsync(Guid voteId, Guid actorUserId, CancellationToken ct = default) =>
        drafts.DeleteDraftAsync(voteId, actorUserId, ct);

    private static string? SafeInfoUrl(string? url) => AssemblyVoteDrafts.SafeInfoUrl(url);

    public async Task<AssemblyVoteActionResult> OpenAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;

        var now = clock.GetCurrentInstant();
        if (vote.ClosesAt <= now) return AssemblyVoteActionResult.Invalid;

        var roster = await rosterBuilder.BuildRosterAsync(vote, ct);
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
        await effects.AfterTransitionAsync(
            "auditing the vote opening", vote.Id,
            _ => audit.LogAsync(
                GovernanceAuditActions.AssemblyVoteOpened, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Opened assembly vote {vote.Id}: {officialCount} official and "
                + $"{roster.Count - officialCount} indicative roster members.",
                adminUserId));

        await effects.AfterTransitionAsync(
            "notifying the roster that the vote opened", vote.Id,
            token => effects.NotifyRosterOpenedAsync(vote, roster, token));
        return AssemblyVoteActionResult.Ok;
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
        if (!await CloseAsync(vote, adminUserId, GovernanceAuditActions.AssemblyVoteStopped,
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

        await effects.AfterTransitionAsync(
            "auditing the extension", vote.Id,
            _ => audit.LogAsync(
                GovernanceAuditActions.AssemblyVoteExtended, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Extended assembly vote {vote.Id} from {previous} to {newClosesAt}.",
                adminUserId));

        // Re-arm the T-24h reminder: stamps up to now mean "told about the old deadline"; later
        // stamps come from a sweep already announcing the new one. Post-transition, not on the
        // request token: there is no replay for an extension.
        await effects.AfterTransitionAsync(
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

        await effects.ClearOpenNotificationAsync(vote, adminUserId, ct);

        await effects.AfterTransitionAsync(
            "auditing the vote cancellation", vote.Id,
            _ => audit.LogAsync(
                GovernanceAuditActions.AssemblyVoteCancelled, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Cancelled assembly vote {vote.Id}: {reason}",
                adminUserId));

        await effects.AfterTransitionAsync(
            "notifying the roster that the vote was cancelled", vote.Id,
            token => effects.NotifyRosterCancelledAsync(vote, token));
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
            GovernanceAuditActions.AssemblyVotePeeked, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Peeked at the live tally of open assembly vote {vote.Id}.", adminUserId);

        return (await resultReader.ComputeResultAsync(vote, ct), true);
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
            GovernanceAuditActions.AssemblyBallotsViewed, AuditEntityTypes.AssemblyVote, voteId,
            $"Listed the individual ballots of assembly vote {voteId}.", actorUserId);

        return await resultReader.DisclosureRowsAsync(voteId, ct);
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

    public async Task<int> RunLapseAndReminderSweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        // Reminders first: closing a lapsed vote makes its roster ineligible for one.
        var failures = new List<Exception>();
        try
        {
            await effects.SendRemindersAsync(now, failures, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            logger.LogError(ex, "Assembly reminder discovery failed; continuing vote closure and recovery.");
        }

        var lapsed = await repository.GetLapsedOpenVotesAsync(now, ct);
        var closed = 0;
        foreach (var vote in lapsed)
        {
            // Counted only when the close landed. A vote an Extend rescued between the query
            // and the write is still open, and a job that reports it as closed is wrong in
            // the one place anybody looks to see what the automation did.
            if (await CloseAsync(vote, closedByUserId: null, GovernanceAuditActions.AssemblyVoteClosed,
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
            try
            {
                await effects.RetryOpenedEmailsAsync(vote, failures, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex, "Assembly vote {VoteId}: opening-email retry preparation failed.", vote.Id);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("Failed to complete one or more assembly vote email deliveries.", failures);
        return closed;
    }

    /// <summary>
    /// The member's own voting record: which votes they were entitled to vote in, whether
    /// their ballot was official, and every revision of what they recorded.
    /// </summary>
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

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
                GovernanceAuditActions.AssemblyVoteRosterMerged, AuditEntityTypes.AssemblyVote, voteId,
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
