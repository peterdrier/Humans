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
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging;
using NodaTime;

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
    IClock clock,
    ILogger<AssemblyVoteService> logger)
    : IAssemblyVoteService, IUserDataContributor, IUserMerge
{
    /// <summary>The recurring job that closes lapsed votes and sends the T-24h reminder.</summary>
    internal const string LapseJobName = "governance-assembly-vote-lapse";

    /// <summary>How far ahead of closing the reminder goes out.</summary>
    private static readonly Duration ReminderLeadTime = Duration.FromHours(24);

    /// <summary>
    /// Enums serialize as names so a stored result stays readable and survives any later
    /// reordering of the CLR enums.
    /// </summary>
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ==========================================================================
    // Member-facing reads
    // ==========================================================================

    public async Task<IReadOnlyList<AssemblyVoteListItem>> GetVotesForMemberAsync(
        Guid userId, CancellationToken ct = default)
    {
        var votes = await SettleAllAsync(ct);
        var items = new List<AssemblyVoteListItem>(votes.Count);

        foreach (var vote in votes)
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
            IsTranslation: !vote.OfficialText.HasCulture(culture)
                           || !culture.Equals(vote.OfficialCulture, StringComparison.OrdinalIgnoreCase),
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

        var existing = await repository.GetBallotForRosterAsync(voteId, roster.Id, ct);
        var ballot = await repository.UpsertBallotAsync(
            voteId, roster.Id, choice, normalizedRanking, now, ct);

        // The action distinguishes a first cast from a change; neither entry records what
        // was chosen. That is the whole point — the audit log is widely readable.
        await audit.LogAsync(
            existing is null ? AuditAction.AssemblyBallotCast : AuditAction.AssemblyBallotChanged,
            AuditEntityTypes.AssemblyBallot,
            ballot.Id,
            existing is null
                ? $"Cast a ballot on assembly vote {voteId} (revision {ballot.Revision})."
                : $"Changed their ballot on assembly vote {voteId} (revision {ballot.Revision}).",
            userId,
            voteId,
            AuditEntityTypes.AssemblyVote);

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

        var now = clock.GetCurrentInstant();
        if (vote.Status != AssemblyVoteStatus.Open || now < vote.ClosesAt) return vote;

        await CloseAsync(vote, closedByUserId: null, AuditAction.AssemblyVoteClosed,
            $"Assembly vote {vote.Id} closed automatically at its announced time.", ct);
        return vote;
    }

    private async Task<IReadOnlyList<AssemblyVote>> SettleAllAsync(CancellationToken ct)
    {
        var votes = await repository.GetAllAsync(ct);
        var now = clock.GetCurrentInstant();

        foreach (var vote in votes.Where(v => v.Status == AssemblyVoteStatus.Open && now >= v.ClosesAt))
        {
            await CloseAsync(vote, closedByUserId: null, AuditAction.AssemblyVoteClosed,
                $"Assembly vote {vote.Id} closed automatically at its announced time.", ct);
        }

        return votes;
    }

    /// <summary>
    /// The single close path: computes the result once, stores it on the vote, resolves the
    /// open-vote notification, and audits. Every way a vote can close funnels through here
    /// so a closed vote always has exactly one stored result.
    /// </summary>
    private async Task CloseAsync(
        AssemblyVote vote,
        Guid? closedByUserId,
        AuditAction action,
        string description,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var result = await ComputeResultAsync(vote, ct);

        vote.Status = AssemblyVoteStatus.Closed;
        vote.ClosedAt = now;
        vote.ClosedByUserId = closedByUserId;
        vote.ResultJson = JsonSerializer.Serialize(result, ResultJsonOptions);
        vote.UpdatedAt = now;

        await repository.UpdateAsync(vote, ct);

        await notificationResolve.ResolveBySourceKeyAsync(
            NotificationSource.AssemblyVoteOpened, vote.Id.ToString(), closedByUserId, ct);

        if (closedByUserId is { } actor)
        {
            await audit.LogAsync(action, AuditEntityTypes.AssemblyVote, vote.Id, description, actor);
        }
        else
        {
            await audit.LogAsync(action, AuditEntityTypes.AssemblyVote, vote.Id, description, LapseJobName);
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
            BuildActa(vote, result));
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
    private static string BuildActa(AssemblyVote vote, AssemblyVoteResult result)
    {
        var acta = new StringBuilder();
        var official = result.Official;

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
                var counts = string.Join(", ", round.Counts.Select(kv => $"{kv.Key}: {kv.Value}"));
                var eliminated = round.EliminatedKey is null
                    ? string.Empty
                    : $"; eliminated: {round.EliminatedKey}";
                acta.AppendLine(CultureInfo.InvariantCulture,
                    $"Round {round.Number}: {counts}; exhausted: {round.Exhausted}{eliminated}");
            }

            if (official.WinnerKey is { } winnerKey)
            {
                acta.AppendLine(CultureInfo.InvariantCulture, $"Winning option: {winnerKey}");
            }
        }

        acta.AppendLine(CultureInfo.InvariantCulture, $"Result: {official.Verdict}");
        if (official.Verdict == AssemblyVoteVerdict.Tie)
        {
            acta.AppendLine("Tied. Under statutes Art. 10.2 the President of the Board casts the "
                            + "deciding vote; record it here.");
        }

        acta.AppendLine(CultureInfo.InvariantCulture, $"Closed at: {vote.ClosedAt}");
        acta.AppendLine(vote.ClosedByUserId is null
            ? "Closed automatically at the announced time."
            : "Closed by an administrator.");

        if (result.Indicative is { } indicative)
        {
            acta.AppendLine(CultureInfo.InvariantCulture,
                $"Indicative (not counted): {indicative.BallotsCast} of {indicative.RosterSize} "
                + $"— {indicative.Verdict}");
        }

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
            vote.Title.Values.ToDictionary(kv => kv.Key, kv => kv.Value),
            vote.OfficialText.Values.ToDictionary(kv => kv.Key, kv => kv.Value),
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
                    o.Key, o.Order, o.Label.Values.ToDictionary(kv => kv.Key, kv => kv.Value)))
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
        await repository.ReplaceOptionsAsync(vote, OptionsFor(vote.Id, draft), ct);

        return AssemblyVoteActionResult.Ok;
    }

    public async Task<AssemblyVoteActionResult> DeleteDraftAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;

        await repository.DeleteAsync(voteId, ct);
        return AssemblyVoteActionResult.Ok;
    }

    /// <summary>
    /// A draft is publishable when it has a title and binding text in its official culture,
    /// a future closing time, and — for a ranked vote — at least two distinctly-keyed options.
    /// </summary>
    private bool IsDraftValid(AssemblyVoteDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.OfficialCulture)) return false;
        if (!draft.Title.TryGetValue(draft.OfficialCulture, out var title)
            || string.IsNullOrWhiteSpace(title)) return false;
        if (!draft.OfficialText.TryGetValue(draft.OfficialCulture, out var text)
            || string.IsNullOrWhiteSpace(text)) return false;
        if (draft.ClosesAt <= clock.GetCurrentInstant()) return false;

        if (draft.Kind != AssemblyVoteKind.RankedChoice) return true;

        return draft.Options.Count >= 2
               && draft.Options.All(o => !string.IsNullOrWhiteSpace(o.Key))
               && draft.Options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count()
                  == draft.Options.Count;
    }

    private static void ApplyDraft(AssemblyVote vote, AssemblyVoteDraft draft, Instant now)
    {
        vote.Title = new GovernanceLocalizedText(draft.Title.ToDictionary(kv => kv.Key, kv => kv.Value));
        vote.OfficialText = new GovernanceLocalizedText(
            draft.OfficialText.ToDictionary(kv => kv.Key, kv => kv.Value));
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
                    Label = new GovernanceLocalizedText(o.Label.ToDictionary(kv => kv.Key, kv => kv.Value))
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

        vote.Status = AssemblyVoteStatus.Open;
        vote.OpenedAt = now;
        vote.OpenedByUserId = adminUserId;
        vote.UpdatedAt = now;

        await repository.OpenWithRosterAsync(vote, roster, ct);

        var officialCount = roster.Count(r => r.IsOfficial);
        await audit.LogAsync(
            AuditAction.AssemblyVoteOpened, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Opened assembly vote {vote.Id}: {officialCount} official and "
            + $"{roster.Count - officialCount} indicative roster members.",
            adminUserId);

        await NotifyRosterOpenedAsync(vote, roster, ct);
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
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        await CloseAsync(vote, adminUserId, AuditAction.AssemblyVoteStopped,
            $"Stopped assembly vote {vote.Id} before its announced closing time.", ct);

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
        vote.ClosesAt = newClosesAt;
        vote.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateAsync(vote, ct);

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

        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        var now = clock.GetCurrentInstant();
        vote.Status = AssemblyVoteStatus.Cancelled;
        vote.ClosedAt = now;
        vote.ClosedByUserId = adminUserId;
        vote.CancelReason = reason;
        vote.UpdatedAt = now;

        // No result is computed: a cancelled vote decided nothing, and the ballots stay only
        // as a record that it was attempted.
        await repository.UpdateAsync(vote, ct);

        await notificationResolve.ResolveBySourceKeyAsync(
            NotificationSource.AssemblyVoteOpened, vote.Id.ToString(), adminUserId, ct);

        await audit.LogAsync(
            AuditAction.AssemblyVoteCancelled, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Cancelled assembly vote {vote.Id}: {reason}", adminUserId);

        await NotifyRosterCancelledAsync(vote, ct);
        return AssemblyVoteActionResult.Ok;
    }

    public async Task<AssemblyVoteResult?> PeekAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return null;

        // A peek at a closed vote is just the results page; the peek log exists to record
        // looking *early*, so only an open vote writes one.
        if (vote.Status != AssemblyVoteStatus.Open)
        {
            return vote.ResultJson is null
                ? null
                : JsonSerializer.Deserialize<AssemblyVoteResult>(vote.ResultJson, ResultJsonOptions);
        }

        var now = clock.GetCurrentInstant();

        // The peek row and the audit entry are written before the tally is handed back, so
        // there is no way to look without leaving the trace that says you did.
        await repository.AddPeekAsync(
            new AssemblyVotePeek
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                AdminUserId = adminUserId,
                PeekedAt = now
            }, ct);

        await audit.LogAsync(
            AuditAction.AssemblyVotePeeked, AuditEntityTypes.AssemblyVote, vote.Id,
            $"Peeked at the live tally of open assembly vote {vote.Id}.", adminUserId);

        return await ComputeResultAsync(vote, ct);
    }

    public async Task<IReadOnlyList<AssemblyBallotDisclosureRow>?> GetBallotsForBoardAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default)
    {
        var vote = await SettleAsync(voteId, ct);
        if (vote is null) return null;
        if (!vote.IsTerminal) return null;

        await audit.LogAsync(
            AuditAction.AssemblyBallotsViewed, AuditEntityTypes.AssemblyVote, voteId,
            $"Listed the individual ballots of assembly vote {voteId}.", actorUserId);

        return await DisclosureRowsAsync(voteId, ct);
    }

    // ==========================================================================
    // Notifying the roster
    // ==========================================================================

    /// <summary>
    /// Emails everyone on the roster that the vote is open and raises one in-app
    /// notification keyed to the vote, which closing later resolves.
    /// </summary>
    /// <remarks>
    /// Per-recipient try/catch, as in Surveys' invite send: one bad address must not leave
    /// the rest of the electorate uninformed about a vote that is already open.
    /// </remarks>
    private async Task NotifyRosterOpenedAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct)
    {
        var recipients = await RecipientsAsync(roster, ct);
        var notified = new List<Guid>(roster.Count);
        var closesAt = FormatClosing(vote.ClosesAt);

        foreach (var (rosterRow, info, address) in recipients)
        {
            try
            {
                await email.SendAsync(
                    messages.AssemblyVoteOpened(
                        address,
                        info.BurnerName,
                        vote.Title.Resolve(info.PreferredLanguage, vote.OfficialCulture),
                        closesAt,
                        rosterRow.IsOfficial,
                        VoteUrl(vote.Id),
                        info.PreferredLanguage),
                    ct);

                notified.Add(rosterRow.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to send the vote-opened email for assembly vote {VoteId} to roster row {RosterId}.",
                    vote.Id, rosterRow.Id);
            }
        }

        if (notified.Count > 0)
        {
            await repository.StampNotifiedAsync(notified, clock.GetCurrentInstant(), ct);
        }

        var userIds = roster.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList();
        if (userIds.Count > 0)
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
        }
    }

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
                        vote.Title.Resolve(info.PreferredLanguage, vote.OfficialCulture),
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

    private static string FormatClosing(Instant closesAt) => closesAt.ToDateTime(MadridZone);

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
        foreach (var vote in lapsed)
        {
            await CloseAsync(vote, closedByUserId: null, AuditAction.AssemblyVoteClosed,
                $"Assembly vote {vote.Id} closed automatically at its announced time.", ct);
        }

        if (lapsed.Count > 0)
        {
            logger.LogInformation("Closed {Count} lapsed assembly vote(s).", lapsed.Count);
        }

        return lapsed.Count;
    }

    /// <summary>
    /// Sends the T-24h reminder to roster members who have not voted, stamping each row so
    /// the reminder cannot repeat however often the job runs.
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
            var closesAt = FormatClosing(vote.ClosesAt);

            foreach (var (rosterRow, info, address) in recipients)
            {
                try
                {
                    await email.SendAsync(
                        messages.AssemblyVoteReminder(
                            address,
                            info.BurnerName,
                            vote.Title.Resolve(info.PreferredLanguage, vote.OfficialCulture),
                            closesAt,
                            rosterRow.IsOfficial,
                            VoteUrl(vote.Id),
                            info.PreferredLanguage),
                        ct);

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
                await repository.StampReminderSentAsync(reminded, now, ct);
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
        if (record.Count == 0) return [];

        var rows = record
            .Select(x => new
            {
                Vote = x.Vote.Title.Resolve(x.Vote.OfficialCulture, x.Vote.OfficialCulture),
                x.Vote.Status,
                x.Vote.ClosesAt,
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
                        x.Ballot.CastAt,
                        x.Ballot.UpdatedAt,
                        History = x.Ballot.History
                            .OrderBy(h => h.Revision)
                            .Select(h => new { h.Revision, h.Choice, h.Ranking, h.RecordedAt })
                            .ToList()
                    }
            })
            .ToList();

        return [new UserDataSlice(GdprExportSections.AssemblyVotes, rows)];
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
                + "the ballot can no longer be attributed to the person."
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
    /// on the same vote's roster the target's row wins and the source's ballot is dropped —
    /// one person may hold only one ballot per vote — and each drop is audited, because it
    /// destroys a recorded ballot.
    /// </summary>
    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var dropped = await repository.ReassignRosterToUserAsync(mergedFromUserId, mergedToUserId, ct);

        foreach (var rosterId in dropped)
        {
            await audit.LogAsync(
                AuditAction.AssemblyVoteRosterMerged, AuditEntityTypes.AssemblyBallot, rosterId,
                "Account merge: both accounts were on the same assembly-vote roster, so the "
                + "merged-from account's roster row and ballot were dropped in favour of the "
                + "surviving account's.",
                actorUserId, mergedToUserId, AuditEntityTypes.AssemblyVote);
        }
    }
}
