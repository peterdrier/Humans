using Humans.Governance.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Google.Contracts;
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
        AssemblyVoteDrafts.MaxTitleLength, LapseJobName);

    /// <summary>The recurring job that closes lapsed votes and sends the T-24h reminder.</summary>
    internal const string LapseJobName = "governance-assembly-vote-lapse";

    /// <summary>
    /// Enums as names so a stored result survives CLR reordering; NodaTime because
    /// <see cref="AssemblyVoteResult.ComputedAt"/> would otherwise read back as <c>Instant.MinValue</c>.
    /// </summary>
    private static readonly JsonSerializerOptions ResultJsonOptions =
        new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }
            .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    private AssemblyVoteMemberWorkflow Participation => new(
        repository, Settlement, users, audit, effects, notificationResolve, clock);

    public Task<IReadOnlyList<AssemblyVoteListItem>> GetVotesForMemberAsync(
        Guid userId, CancellationToken ct = default) =>
        Participation.GetVotesForMemberAsync(userId, ct);

    public Task<AssemblyVoteDetail?> GetVoteForMemberAsync(
        Guid voteId, Guid userId, CancellationToken ct = default) =>
        Participation.GetVoteForMemberAsync(voteId, userId, ct);

    public Task<BallotSubmissionOutcome> CastBallotAsync(
        Guid voteId, Guid userId, AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking, CancellationToken ct = default) =>
        Participation.CastBallotAsync(voteId, userId, choice, ranking, ct);

    private AssemblyVoteSettlement Settlement => new(
        repository, audit, clock, effects, resultReader, ResultJsonOptions, LapseJobName);

    public async Task<AssemblyVoteResultsView?> GetResultsAsync(
        Guid voteId, Guid userId, bool viewerIsBoardOrAdmin, CancellationToken ct = default)
    {
        var vote = await Settlement.SettleAsync(voteId, ct);
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

        var roster = await Participation.EffectiveRosterAsync(voteId, userId, ct);
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
            await Participation.DetailAsync(vote, roster, ownBallot, ct),
            result,
            await resultReader.PeekViewsAsync(voteId, ct),
            disclosed,
            AssemblyVotePresentation.BuildActa(vote, result, await resultReader.ClosedByNameAsync(vote, ct)));
    }

    public async Task<IReadOnlyList<AssemblyVoteListItem>> GetAllForAdminAsync(
        CancellationToken ct = default)
    {
        var votes = await Settlement.SettleAllAsync(ct);
        var items = new List<AssemblyVoteListItem>(votes.Count);

        // Every state, drafts included: this is the page Create redirects to, and its
        // Edit / Delete / Open controls are the only way a draft is ever progressed.
        foreach (var vote in votes)
        {
            items.Add(new AssemblyVoteListItem(
                vote.Id,
                AssemblyVoteMemberWorkflow.Resolve(vote.Title, vote.OfficialCulture),
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

    private AssemblyVoteAdministration Administration => new(
        repository, rosterBuilder, Settlement, effects, audit, clock);

    public Task<AssemblyVoteActionResult> OpenAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default) =>
        Administration.OpenAsync(voteId, adminUserId, ct);

    public Task<AssemblyVoteActionResult> StopAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default) =>
        Administration.StopAsync(voteId, adminUserId, ct);

    public Task<AssemblyVoteActionResult> ExtendAsync(
        Guid voteId, Instant newClosesAt, Guid adminUserId, CancellationToken ct = default) =>
        Administration.ExtendAsync(voteId, newClosesAt, adminUserId, ct);

    public Task<AssemblyVoteActionResult> CancelAsync(
        Guid voteId, string reason, Guid adminUserId, CancellationToken ct = default) =>
        Administration.CancelAsync(voteId, reason, adminUserId, ct);

    public async Task<(AssemblyVoteResult? Result, bool Recorded)> PeekAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await Settlement.SettleAsync(voteId, ct);
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
        var vote = await Settlement.SettleAsync(voteId, ct);
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

    public async Task<int> RunLapseAndReminderSweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        // Reminders first: closing a lapsed vote makes its roster ineligible for one.
        var failures = new List<Exception>();
        var closed = 0;
        try
        {
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
            foreach (var vote in lapsed)
            {
                ct.ThrowIfCancellationRequested();
                // Counted only when the close landed. A vote an Extend rescued between the query
                // and the write is still open, and a job that reports it as closed is wrong in
                // the one place anybody looks to see what the automation did.
                if (await Settlement.CloseAsync(vote, closedByUserId: null, GovernanceAuditActions.AssemblyVoteClosed,
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
                ct.ThrowIfCancellationRequested();
                await Settlement.FinishInterruptedCloseAsync(vote, ct);
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
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            if (failures.Count > 0)
                throw new AggregateException("Assembly vote sweep failed before shutdown.", failures.Append(ex));
            logger.LogWarning("Assembly vote sweep interrupted by shutdown");
            throw;
        }
        catch (Exception ex) when (failures.Count > 0)
        {
            throw new AggregateException("Assembly vote sweep failed after earlier delivery failures.", failures.Append(ex));
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
            [AssemblyVoteDataExport.AssemblyVotes] =
                "Partially retained: the member's roster row is anonymized (the link to the "
                + "account is removed) but the row and its ballot are kept, because the vote "
                + "is the association's legal record of an agreement it adopted and the "
                + "turnout and stored result must stay arithmetically valid "
                + "(Ley Organica 1/2002 Art. 14; GDPR Art. 17(3)(b) and (e)). After erasure "
                + "the ballot can no longer be attributed to the person.",

            [AssemblyVoteDataExport.AssemblyVoteActions] =
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
