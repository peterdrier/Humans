using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
using Humans.Governance.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

using Xunit;

namespace Humans.Governance.Tests.Services;

/// <summary>
/// Ballot casting/changing, lifecycle transitions, the lapse sweep, and GDPR erasure — see
/// <c>Docs/features/assembly-votes.md</c>'s Implementation checklist.
/// </summary>
public sealed class AssemblyVoteServiceTests : IDisposable
{
    private readonly AssemblyVoteServiceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // ==========================================================================
    // Casting a ballot
    // ==========================================================================

    [HumansFact]
    public async Task CastBallotAsync_RosterMember_Records()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded);
        var ballot = await _fx.Db.AssemblyBallots
            .FirstAsync(b => b.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken);
        ballot.Revision.Should().Be(1);
        ballot.Choice.Should().Be(AssemblyBallotChoice.Yes);
    }

    [HumansFact]
    public async Task CastBallotAsync_NonRosterMember_IsRejected()
    {
        var vote = await _fx.AddVoteAsync();

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, Guid.NewGuid(), AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.NotOnRoster);
        (await _fx.Db.AssemblyBallots.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task CastBallotAsync_ChangingABallot_BumpsRevisionAppendsHistoryAndKeepsOriginalCastAt()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);
        var firstCastAt = (await _fx.Db.AssemblyBallots
            .AsNoTracking()
            .FirstAsync(b => b.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken)).CastAt;

        _fx.Clock.AdvanceHours(1);
        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.No, null, Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded);
        var ballot = await _fx.Db.AssemblyBallots
            .AsNoTracking()
            .Include(b => b.History)
            .FirstAsync(b => b.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken);
        ballot.Revision.Should().Be(2);
        ballot.Choice.Should().Be(AssemblyBallotChoice.No);
        ballot.CastAt.Should().Be(firstCastAt);
        ballot.UpdatedAt.Should().Be(_fx.Clock.GetCurrentInstant());
        ballot.History.Should().HaveCount(2);
        ballot.History.Select(h => h.Revision).Should().BeEquivalentTo([1, 2]);

        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyBallotCast,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyBallotChanged,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CastBallotAsync_AuditEntry_NamesVoterButNeverTheChoice()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);

        await _fx.Audit.Received().LogAsync(
            AuditAction.AssemblyBallotCast,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => !d.Contains("Yes", StringComparison.Ordinal)),
            userId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CastBallotAsync_AuditEntry_NamesTheVoteNeverTheBallotRow()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);

        // An audit row keeps its ActorUserId forever, so naming the ballot id would leave a
        // permanent join from an erased person to the row holding their choice.
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyBallotCast,
            "AssemblyVote", vote.Id, Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CreateDraftAsync_WithAnOverlongOptionKey_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var draft = _fx.DraftFor(
            vote, AssemblyVoteKind.RankedChoice, ["a", new string('k', 101)]);

        var voteId = await _fx.Service.CreateDraftAsync(
            draft, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        voteId.Should().BeNull(
            "the key column is varchar(100) — an over-long key is a validation message, not a 500");
    }

    [HumansFact]
    public async Task CreateDraftAsync_WithAnOverlongInfoUrl_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var draft = _fx.DraftFor(vote, AssemblyVoteKind.YesNo, []) with
        {
            InfoUrl = "https://example.org/" + new string('u', 2000)
        };

        var voteId = await _fx.Service.CreateDraftAsync(
            draft, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        voteId.Should().BeNull(
            "the InfoUrl column is varchar(2000) — an over-long link is a validation message, not a 500");
    }

    [HumansFact]
    public async Task CreateDraftAsync_WithAnOverlongOfficialCulture_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var draft = _fx.DraftFor(vote, AssemblyVoteKind.YesNo, []) with
        {
            OfficialCulture = new string('x', 11)
        };

        var voteId = await _fx.Service.CreateDraftAsync(
            draft, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        voteId.Should().BeNull("the OfficialCulture column is varchar(10)");
    }

    [HumansFact]
    public async Task CreateDraftAsync_WithAnUndefinedKind_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var draft = _fx.DraftFor(vote, AssemblyVoteKind.YesNo, []) with
        {
            Kind = (AssemblyVoteKind)99
        };

        var voteId = await _fx.Service.CreateDraftAsync(
            draft, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        voteId.Should().BeNull(
            "an undefined kind stores no options but counts as instant-runoff, which opens a vote nobody can win");
    }

    [HumansFact]
    public async Task UpsertBallotAsync_OnAVoteThatHasClosed_WritesNothing()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);
        var roster = await _fx.AddRosterRowAsync(vote.Id, Guid.NewGuid(), isOfficial: true);

        var ballot = await _fx.Repository.UpsertBallotAsync(
            vote.Id, roster.Id, AssemblyBallotChoice.Yes, null, _fx.Clock.GetCurrentInstant(),
            Xunit.TestContext.Current.CancellationToken);

        ballot.Should().BeNull("closure persists the closed status before it counts the ballots");
        _fx.Db.AssemblyBallots.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetResultsAsync_AfterACloseThatNeverStoredItsResult_FinishesTheClose()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);
        var userId = Guid.NewGuid();
        var roster = await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        // Long enough ago that no request could still be finishing this close.
        _fx.Clock.AdvanceMinutes(10);

        var results = await _fx.Service.GetResultsAsync(
            vote.Id, userId, viewerIsBoardOrAdmin: false, Xunit.TestContext.Current.CancellationToken);

        results.Should().NotBeNull("a process that died between the two writes must not leave the page blank forever");
        _fx.Db.AssemblyVotes.Single(v => v.Id == vote.Id).ResultJson.Should().NotBeNull();
    }

    [HumansFact]
    public async Task GetResultsAsync_AfterACloseThatNeverStoredItsResult_StillAuditsTheStop()
    {
        var adminId = Guid.NewGuid();
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);
        vote.ClosedByUserId = adminId;
        _fx.Db.AssemblyVotes.Update(vote);
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();
        _fx.Clock.AdvanceMinutes(10);

        await _fx.Service.GetResultsAsync(
            vote.Id, adminId, viewerIsBoardOrAdmin: false, Xunit.TestContext.Current.CancellationToken);

        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteStopped, AuditEntityTypes.AssemblyVote, vote.Id,
            Arg.Any<string>(), adminId);
    }

    // ==========================================================================
    // GDPR export
    // ==========================================================================

    [HumansFact]
    public async Task ContributeForUserAsync_IncludesTheVotesAnOfficerRanAndPeekedAt()
    {
        var officer = Guid.NewGuid();
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var tracked = await _fx.Db.AssemblyVotes.SingleAsync(
            v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        tracked.OpenedByUserId = officer;
        tracked.ClosedByUserId = officer;
        _fx.Db.AssemblyVotePeeks.Add(new AssemblyVotePeek
        {
            Id = Guid.NewGuid(),
            VoteId = vote.Id,
            AdminUserId = officer,
            PeekedAt = _fx.Clock.GetCurrentInstant()
        });
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var slices = await _fx.Service.ContributeForUserAsync(
            officer, Xunit.TestContext.Current.CancellationToken);

        // The officer is on no roster, so the voting-record slice is empty — without the
        // actor slice their activity would be missing from the export entirely.
        var actions = slices.Should()
            .ContainSingle(s => s.SectionName == GdprExportSections.AssemblyVoteActions)
            .Which.Data;
        var json = System.Text.Json.JsonSerializer.Serialize(actions);
        json.Should().Contain("Opened").And.Contain("Closed");

        // Instants are serialized by the export's plain System.Text.Json options, which know
        // nothing about NodaTime: an unformatted Instant lands in the file as "{}". Seen for
        // real on the PR preview before this was fixed.
        json.Should().MatchRegex("\"PeekedAt\":\"[0-9]{4}-");
        json.Should().MatchRegex("\"ClosedAt\":\"[0-9]{4}-");
    }

    [HumansFact]
    public async Task ContributeForUserAsync_WritesBallotTimestampsAsText()
    {
        var vote = await _fx.AddVoteAsync();
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        var slices = await _fx.Service.ContributeForUserAsync(
            userId, Xunit.TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(
            slices.Single(x => string.Equals(
                x.SectionName, GdprExportSections.AssemblyVotes, StringComparison.Ordinal)).Data);
        json.Should().MatchRegex("\"CastAt\":\"[0-9]{4}-");
        json.Should().MatchRegex("\"ClosesAt\":\"[0-9]{4}-");
        json.Should().MatchRegex("\"RecordedAt\":\"[0-9]{4}-");
    }

    [HumansFact]
    public async Task CastBallotAsync_VoteNotOpen_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.VoteNotOpen);
    }

    // ==========================================================================
    // Ballot validation
    // ==========================================================================

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_DuplicateKeysInRanking_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["a", "a"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_UnknownOptionKey_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["a", "nonexistent"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_YesNoVote_RankedChoice_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(kind: AssemblyVoteKind.YesNo);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["a"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_YesChoice_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.InvalidBallot);
    }

    [HumansFact]
    public async Task CastBallotAsync_RankedVote_PartialRanking_IsAccepted()
    {
        var vote = await _fx.AddVoteAsync(
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1), ("c", 2)]);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Ranked, ["b"],
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded);
    }

    // ==========================================================================
    // Lifecycle: lapse without the job
    // ==========================================================================

    [HumansFact]
    public async Task Read_AfterClosesAtPasses_SettlesTheVoteClosedWithoutTheLapseJob()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        _fx.Clock.AdvanceMinutes(10);

        var detail = await _fx.Service.GetVoteForMemberAsync(
            vote.Id, userId, Xunit.TestContext.Current.CancellationToken);

        detail!.Status.Should().Be(AssemblyVoteStatus.Closed);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
        stored.ResultJson.Should().NotBeNull();
        stored.ClosedByUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task Read_TwiceAfterLapse_DoesNotRecomputeOrDoubleStoreTheResult()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        _fx.Clock.AdvanceMinutes(10);

        await _fx.Service.GetVoteForMemberAsync(vote.Id, userId, Xunit.TestContext.Current.CancellationToken);
        var firstResultJson = (await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken)).ResultJson;

        _fx.Clock.AdvanceMinutes(5);
        await _fx.Service.GetVoteForMemberAsync(vote.Id, userId, Xunit.TestContext.Current.CancellationToken);

        var secondResultJson = (await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken)).ResultJson;
        secondResultJson.Should().Be(firstResultJson);
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteClosed,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RunLapseAndReminderSweepAsync_ClosesLapsedVotes()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        _fx.Clock.AdvanceMinutes(10);

        var closedCount = await _fx.Service.RunLapseAndReminderSweepAsync(
            Xunit.TestContext.Current.CancellationToken);

        closedCount.Should().Be(1);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
    }

    // ==========================================================================
    // Terminal states
    // ==========================================================================

    [HumansFact]
    public async Task StopAsync_AlreadyClosed_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.StopAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task StopAsync_AlreadyCancelled_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Cancelled);

        var result = await _fx.Service.StopAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task CancelAsync_AlreadyClosed_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.CancelAsync(
            vote.Id, "reason", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task ExtendAsync_AlreadyClosed_ReturnsWrongState()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.ExtendAsync(
            vote.Id, _fx.Clock.GetCurrentInstant() + Duration.FromDays(2), Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    [HumansFact]
    public async Task CastBallotAsync_OnCancelledVote_IsRejected()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Cancelled);
        var userId = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, userId, AssemblyBallotChoice.Yes, null, Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.VoteNotOpen);
    }

    [HumansFact]
    public async Task OpenAsync_NonDraftVote_ReturnsWrongStateAndNeverReopens()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var result = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
    }

    // ==========================================================================
    // GDPR erasure
    // ==========================================================================

    [HumansFact]
    public async Task EraseForUserAsync_TombstonesRosterRow_AndLeavesTheStoredResultIntact()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);
        var userId = Guid.NewGuid();
        var roster = await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        // Close the vote for real so it carries a stored result to protect.
        _fx.Db.ChangeTracker.Clear();
        var stored = await _fx.Db.AssemblyVotes.FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ResultJson = "{\"official\":{}}";
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        await _fx.Service.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        var rosterAfter = await _fx.Db.AssemblyVoteRosterEntries
            .AsNoTracking()
            .FirstAsync(r => r.Id == roster.Id, Xunit.TestContext.Current.CancellationToken);
        rosterAfter.UserId.Should().BeNull();

        var voteAfter = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        voteAfter.ResultJson.Should().Be("{\"official\":{}}");

        var ballotAfter = await _fx.Db.AssemblyBallots
            .AsNoTracking()
            .FirstAsync(b => b.RosterId == roster.Id, Xunit.TestContext.Current.CancellationToken);
        ballotAfter.Choice.Should().Be(AssemblyBallotChoice.Yes);
    }

    [HumansFact]
    public void ErasureDeclaration_DeclaresPartialRetentionForAssemblyVotes()
    {
        _fx.Service.ErasureDeclaration.Should().ContainKey(
            GdprExportSections.AssemblyVotes);
    }

    // ==========================================================================
    // An elapsed vote is closed for every purpose, admin actions included
    // ==========================================================================

    [HumansFact]
    public async Task CancelAsync_AfterClosesAtPasses_IsRejectedAndTheStoredResultSurvives()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var userId = Guid.NewGuid();
        var roster = await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        _fx.Clock.AdvanceMinutes(10);

        var result = await _fx.Service.CancelAsync(
            vote.Id, "changed our minds", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
        stored.ResultJson.Should().NotBeNull();
        stored.CancelReason.Should().BeNull();
    }

    [HumansFact]
    public async Task StopAsync_AfterClosesAtPasses_IsRejectedAndTheLapseIsNotAttributedToTheAdmin()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var adminId = Guid.NewGuid();

        _fx.Clock.AdvanceMinutes(10);

        var result = await _fx.Service.StopAsync(
            vote.Id, adminId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.WrongState);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed);
        stored.ClosedByUserId.Should().BeNull();
    }

    // ==========================================================================
    // A ranked draft can be edited more than once
    // ==========================================================================

    [HumansFact]
    public async Task UpdateDraftAsync_OnARankedDraft_ReplacesTheOptions()
    {
        var vote = await _fx.AddVoteAsync(
            status: AssemblyVoteStatus.Draft,
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);

        var result = await _fx.Service.UpdateDraftAsync(
            vote.Id,
            _fx.DraftFor(vote, AssemblyVoteKind.RankedChoice, ["c", "d"]),
            Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.Ok);
        var keys = await _fx.Db.AssemblyVoteOptions
            .AsNoTracking()
            .Where(o => o.VoteId == vote.Id)
            .Select(o => o.Key)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        keys.Should().BeEquivalentTo(["c", "d"]);
    }

    [HumansFact]
    public async Task Read_AfterClosesAtPasses_StampsTheAnnouncedClosingTimeNotTheObservation()
    {
        var closesAt = _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5);
        var vote = await _fx.AddVoteAsync(closesAt: closesAt);

        // Nobody looks for 45 minutes. The acta must still say the vote closed when it said
        // it would, not when the sweep got round to it.
        _fx.Clock.AdvanceMinutes(50);

        await _fx.Service.GetVoteForMemberAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ClosedAt.Should().Be(closesAt);
    }

    [HumansFact]
    public async Task StopAsync_StampsTheMomentTheAdminStopped()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(6));
        var adminId = Guid.NewGuid();

        var result = await _fx.Service.StopAsync(
            vote.Id, adminId, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.Ok);
        var stored = await _fx.Db.AssemblyVotes
            .AsNoTracking()
            .FirstAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ClosedAt.Should().Be(_fx.Clock.GetCurrentInstant(),
            "an Admin stop really does close the vote now, unlike a lapse");
    }

    [HumansFact]
    public async Task StoredResult_RoundTripsItsComputedAtInstant()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5));
        var userId = Guid.NewGuid();
        var roster = await _fx.AddRosterRowAsync(vote.Id, userId, isOfficial: true);
        await _fx.AddBallotAsync(vote.Id, roster.Id, AssemblyBallotChoice.Yes);

        _fx.Clock.AdvanceMinutes(10);

        var results = await _fx.Service.GetResultsAsync(
            vote.Id, userId, viewerIsBoardOrAdmin: false, Xunit.TestContext.Current.CancellationToken);

        results!.Result.ComputedAt.Should().NotBe(Instant.MinValue,
            "an Instant needs NodaTime's converters — the default ones silently read back MinValue");
        results.Result.ComputedAt.Should().Be(_fx.Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task CreateDraftAsync_OnARankedDraftWithAnUnlabelledOption_IsRejected()
    {
        var draft = new AssemblyVoteDraft(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Test vote" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Text" },
            "en",
            null,
            AssemblyVoteKind.RankedChoice,
            RequiredMajority.Simple,
            IndicativeAudience.None,
            BallotDisclosure.BoardOnly,
            null,
            _fx.Clock.GetCurrentInstant() + Duration.FromDays(1),
            [
                new AssemblyVoteDraftOption("a", 0,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Option A" }),
                new AssemblyVoteDraftOption("b", 1,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            ]);

        var voteId = await _fx.Service.CreateDraftAsync(
            draft, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        voteId.Should().BeNull(
            "an option with a key but no label would open as a blank line on a binding ballot");
    }

    [HumansFact]
    public async Task ReassignAsync_AuditsTheDroppedBallotAndItsVote()
    {
        var vote = await _fx.AddVoteAsync();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var sourceRoster = await _fx.AddRosterRowAsync(vote.Id, source, isOfficial: true);
        await _fx.AddRosterRowAsync(vote.Id, target, isOfficial: true);
        var ballot = await _fx.AddBallotAsync(vote.Id, sourceRoster.Id, AssemblyBallotChoice.Yes);

        var actor = Guid.NewGuid();
        await _fx.Service.ReassignAsync(
            source, target, actor, _fx.Clock.GetCurrentInstant(),
            Xunit.TestContext.Current.CancellationToken);

        // The entity is the ballot that was destroyed and the related entity is its vote —
        // the roster row's own id resolves to nothing once the row is gone.
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteRosterMerged,
            "AssemblyBallot",
            ballot.Id,
            Arg.Any<string>(),
            actor,
            vote.Id,
            "AssemblyVote");
    }

    // ==========================================================================
    // Translation pre-fill
    // ==========================================================================

    [HumansFact]
    public async Task PreFillTranslationsAsync_FillsBlanksAndLeavesAuthoredTextAlone()
    {
        var vote = await _fx.AddVoteAsync(
            status: AssemblyVoteStatus.Draft,
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);

        // The author already wrote the Spanish title themselves; only the blanks may be filled.
        var tracked = await _fx.Db.AssemblyVotes.SingleAsync(
            v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        tracked.Title = new GovernanceLocalizedText(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "Test vote",
            ["es"] = "Mi propio título"
        });
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        StubTranslation("es");

        var filled = await _fx.Service.PreFillTranslationsAsync(
            vote.Id, ["en", "es"], Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        // Official text and the two option labels — the authored Spanish title is not one of them.
        filled.Should().Be(3);

        var stored = await _fx.Db.AssemblyVotes
            .Include(v => v.Options)
            .AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);

        stored.Title.Resolve("es", "en").Should().Be("Mi propio título",
            "a machine translation never overwrites what the Board wrote");
        stored.Title.Resolve("en", "en").Should().Be("Test vote");
        stored.OfficialText.Resolve("es", "en").Should().Be("ES:Text");
        stored.Options.Single(o => string.Equals(o.Key, "a", StringComparison.Ordinal))
            .Label.Resolve("es", "en").Should().Be("ES:a");
        stored.Options.Single(o => string.Equals(o.Key, "b", StringComparison.Ordinal))
            .Label.Resolve("es", "en").Should().Be("ES:b");
    }

    [HumansFact]
    public async Task PreFillTranslationsAsync_OnAnOpenVote_ChangesNothing()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Open);
        StubTranslation("es");

        var filled = await _fx.Service.PreFillTranslationsAsync(
            vote.Id, ["en", "es"], Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        filled.Should().Be(0,
            "translating a motion the electorate is already voting on would change what some members read");
        await _fx.Translation.DidNotReceive().TranslateAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Title.HasCulture("es").Should().BeFalse();
    }

    [HumansFact]
    public async Task PreFillTranslationsAsync_WhenTheDraftIsEditedWhileTranslating_WritesNothing()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);

        // The Board rewrites the motion while the translator is waiting on Google. The draft
        // is still a draft, so only the revision it was read at can catch this.
        _fx.Translation.TranslateAsync(
                Arg.Any<IReadOnlyList<string>>(), "en", "es", Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var tracked = await _fx.Db.AssemblyVotes.SingleAsync(
                    v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
                tracked.Title = new GovernanceLocalizedText(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["en"] = "The motion the Board actually wants"
                    });
                tracked.UpdatedAt = _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(5);
                await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
                _fx.Db.ChangeTracker.Clear();

                return (IReadOnlyList<string>)[.. ci.Arg<IReadOnlyList<string>>().Select(t => "ES:" + t)];
            });

        var filled = await _fx.Service.PreFillTranslationsAsync(
            vote.Id, ["en", "es"], Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        filled.Should().Be(0, "nothing was stored, so nothing was filled");

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Title.Resolve("en", "en").Should().Be("The motion the Board actually wants",
            "the Board's own wording outranks a machine translation of the wording it replaced");
        stored.Title.HasCulture("es").Should().BeFalse();
    }

    /// <summary>Echoes each source string back prefixed, so a filled blank is recognisable.</summary>
    private void StubTranslation(string target)
    {
        var prefix = target.ToUpperInvariant() + ":";
        _fx.Translation.TranslateAsync(
                Arg.Any<IReadOnlyList<string>>(), "en", target, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<string>>(
                [.. ci.Arg<IReadOnlyList<string>>().Select(t => prefix + t)]));
    }

    // ==========================================================================
    // Post-transition side effects are never allowed to undo the transition
    // ==========================================================================

    [HumansFact]
    public async Task OpenAsync_Succeeds_EvenWhenRecipientLookupThrows()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var asociado = Guid.NewGuid();
        _fx.StubActiveUsers(asociado);
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));

        // Resolving recipients happens after the vote is already persisted Open, and Open is
        // irreversible with no retry path.
        _fx.UserEmails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<Guid, string>>>(
                _ => throw new InvalidOperationException("email lookup is down"));

        var result = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.Ok);

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Open,
            "the roster is snapshotted and the vote is open — a failed lookup cannot unwind that");
    }

    [HumansFact]
    public async Task CancelAsync_WithAnOverlongReason_IsRejected()
    {
        var vote = await _fx.AddVoteAsync();

        var result = await _fx.Service.CancelAsync(
            vote.Id, new string('x', 4001), Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(AssemblyVoteActionResult.Invalid,
            "the column is varchar(4000) — an over-long reason is a rejection, not a 500");

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Open);
    }

    [HumansFact]
    public async Task CancelAsync_WithANearMaximumReason_StillAuditsWithinTheColumn()
    {
        var vote = await _fx.AddVoteAsync();
        var reason = new string('x', 4000);

        await _fx.Service.CancelAsync(
            vote.Id, reason, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        // The audit description prefixes the reason, so the untrimmed string would overrun
        // audit_log.description — and AuditLogService swallows that, losing the entry.
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteCancelled, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Length <= 4000), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ReassignAsync_MovesTheVoteActorReferencesToTheSurvivingAccount()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Closed);

        var tracked = await _fx.Db.AssemblyVotes.SingleAsync(
            v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        tracked.CreatedByUserId = source;
        tracked.OpenedByUserId = source;
        tracked.ClosedByUserId = source;
        _fx.Db.AssemblyVotePeeks.Add(new AssemblyVotePeek
        {
            Id = Guid.NewGuid(),
            VoteId = vote.Id,
            AdminUserId = source,
            PeekedAt = _fx.Clock.GetCurrentInstant()
        });
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        await _fx.Service.ReassignAsync(
            source, target, Guid.NewGuid(), _fx.Clock.GetCurrentInstant(),
            Xunit.TestContext.Current.CancellationToken);

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ClosedByUserId.Should().Be(target,
            "the acta names the closer, and the source account is about to become a tombstone");
        stored.OpenedByUserId.Should().Be(target);
        stored.CreatedByUserId.Should().Be(target,
            "otherwise the author's GDPR export loses every vote they drafted");

        var peek = await _fx.Db.AssemblyVotePeeks.AsNoTracking()
            .SingleAsync(p => p.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken);
        peek.AdminUserId.Should().Be(target,
            "the peek list is published on the results page and must name the surviving human");
    }

    // ==========================================================================
    // A snapshot taken before somebody else's transition never undoes it
    // ==========================================================================

    [HumansFact]
    public async Task UpdateAsync_WithASnapshotTakenBeforeTheVoteClosed_WritesNothing()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(6));

        // What an Admin's Extend or Cancel form would be holding: the vote as it was Open.
        var stale = await _fx.Repository.GetByIdAsync(
            vote.Id, Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        await _fx.Service.StopAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        var written = await _fx.Repository.UpdateAsync(
            stale!, AssemblyVoteStatus.Open, ct: Xunit.TestContext.Current.CancellationToken);

        written.Should().BeFalse();
        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Closed,
            "a closed vote never reopens, whatever an earlier read was holding");
        stored.ResultJson.Should().NotBeNull("and its stored tally is not blanked either");
    }

    [HumansFact]
    public async Task UpdateAsync_WithALapseCloseReadBeforeAnExtend_WritesNothing()
    {
        var closesAt = _fx.Clock.GetCurrentInstant() + Duration.FromHours(1);
        var vote = await _fx.AddVoteAsync(closesAt: closesAt);

        var stale = await _fx.Repository.GetByIdAsync(
            vote.Id, Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        await _fx.Service.ExtendAsync(
            vote.Id, closesAt + Duration.FromHours(4), Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        // The lapse sweep decided from the deadline it read, which the Extend has since moved.
        stale!.Status = AssemblyVoteStatus.Closed;
        stale.ClosedAt = closesAt;
        stale.ClosedByUserId = null;

        var written = await _fx.Repository.UpdateAsync(
            stale, AssemblyVoteStatus.Open, ct: Xunit.TestContext.Current.CancellationToken);

        written.Should().BeFalse();
        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Open,
            "the extension is the newer fact, and the electorate was told the vote is still open");
    }

    [HumansFact]
    public async Task ReplaceOptionsAsync_WithADraftEditReadBeforeTheVoteOpened_WritesNothing()
    {
        var vote = await _fx.AddVoteAsync(
            status: AssemblyVoteStatus.Draft,
            kind: AssemblyVoteKind.RankedChoice,
            options: [("a", 0), ("b", 1)]);

        var asociado = Guid.NewGuid();
        _fx.StubActiveUsers(asociado);
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));

        var stale = await _fx.Repository.GetByIdAsync(
            vote.Id, Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        var opened = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        opened.Should().Be(AssemblyVoteActionResult.Ok);
        _fx.Db.ChangeTracker.Clear();

        var written = await _fx.Repository.ReplaceOptionsAsync(
            stale!,
            [new AssemblyVoteOption
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                Key = "c",
                Order = 0,
                Label = new GovernanceLocalizedText(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "C" })
            }],
            Xunit.TestContext.Current.CancellationToken);

        written.Should().BeFalse();
        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Open, "an edit never un-opens a vote");
        var keys = await _fx.Db.AssemblyVoteOptions.AsNoTracking()
            .Where(o => o.VoteId == vote.Id)
            .Select(o => o.Key)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        keys.Should().BeEquivalentTo(["a", "b"],
            "the electorate is already reading these options");
    }

    [HumansFact]
    public async Task DeleteAsync_OnAVoteThatOpenedSinceItWasRead_DeletesNothing()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var asociado = Guid.NewGuid();
        _fx.StubActiveUsers(asociado);
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));

        var opened = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        opened.Should().Be(AssemblyVoteActionResult.Ok);
        _fx.Db.ChangeTracker.Clear();

        var deleted = await _fx.Repository.DeleteAsync(
            vote.Id, Xunit.TestContext.Current.CancellationToken);

        deleted.Should().BeFalse();
        (await _fx.Db.AssemblyVotes.AsNoTracking()
            .AnyAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().BeTrue("the roster is frozen and the electorate has been told to vote");
    }

    [HumansFact]
    public async Task OpenWithRosterAsync_OnAVoteSomebodyElseOpenedFirst_WritesNoSecondRoster()
    {
        var vote = await _fx.AddVoteAsync(status: AssemblyVoteStatus.Draft);
        var asociado = Guid.NewGuid();
        _fx.StubActiveUsers(asociado);
        _fx.Applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([asociado]));

        var opened = await _fx.Service.OpenAsync(
            vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        opened.Should().Be(AssemblyVoteActionResult.Ok);
        _fx.Db.ChangeTracker.Clear();

        // A second Open that was building its roster while the first one committed.
        var stored = await _fx.Repository.GetByIdAsync(
            vote.Id, Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        var written = await _fx.Repository.OpenWithRosterAsync(
            stored!,
            [new AssemblyVoteRoster
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                UserId = Guid.NewGuid(),
                IsOfficial = true
            }],
            Xunit.TestContext.Current.CancellationToken);

        written.Should().BeFalse();
        var roster = await _fx.Db.AssemblyVoteRosterEntries.AsNoTracking()
            .Where(r => r.VoteId == vote.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        roster.Should().ContainSingle("the roster is written exactly once, at the open that won")
            .Which.UserId.Should().Be(asociado);
    }

    [HumansFact]
    public async Task RunLapseAndReminderSweepAsync_AuditsTheRemindersItSent()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(12));
        var member = Guid.NewGuid();
        _fx.StubActiveUsers(member);
        await _fx.AddRosterRowAsync(vote.Id, member, isOfficial: true);

        await _fx.Service.RunLapseAndReminderSweepAsync(Xunit.TestContext.Current.CancellationToken);

        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteRemindersSent,
            AuditEntityTypes.AssemblyVote,
            vote.Id,
            Arg.Any<string>(),
            AssemblyVoteService.LapseJobName);
    }

    [HumansFact]
    public async Task RunLapseAndReminderSweepAsync_StopsRemindingOnceTheVoteIsNoLongerOpen()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(12));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _fx.StubActiveUsers(first, second);
        await _fx.AddRosterRowAsync(vote.Id, first, isOfficial: true);
        await _fx.AddRosterRowAsync(vote.Id, second, isOfficial: true);

        // An Admin cancels the vote while the first reminder is going out. Nobody after that
        // may be told it closes soon.
        var sent = 0;
        _fx.Email.When(e => e.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                if (++sent != 1) return;

                var open = _fx.Db.AssemblyVotes.Single(v => v.Id == vote.Id);
                open.Status = AssemblyVoteStatus.Cancelled;
                _fx.Db.SaveChanges();
                _fx.Db.ChangeTracker.Clear();
            });

        await _fx.Service.RunLapseAndReminderSweepAsync(Xunit.TestContext.Current.CancellationToken);

        sent.Should().Be(1, "eligibility is re-read per recipient, not trusted from the list");
    }

    [HumansFact]
    public async Task StopAsync_OnAVoteAnotherAdminAlreadyStopped_ChangesNothingAndAuditsOnce()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(6));
        var first = Guid.NewGuid();

        // Both admins read the vote as Open; the first one's stop commits.
        var second = await _fx.Repository.GetByIdAsync(
            vote.Id, Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        (await _fx.Service.StopAsync(vote.Id, first, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(AssemblyVoteActionResult.Ok);
        _fx.Db.ChangeTracker.Clear();

        // The second request's write, built from that same Open read.
        second!.Status = AssemblyVoteStatus.Closed;
        second.ClosedAt = _fx.Clock.GetCurrentInstant() + Duration.FromMinutes(1);
        second.ClosedByUserId = Guid.NewGuid();

        var written = await _fx.Repository.UpdateAsync(
            second, AssemblyVoteStatus.Open, ct: Xunit.TestContext.Current.CancellationToken);

        written.Should().BeFalse("the row is Closed, not the Open this write read");
        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ClosedByUserId.Should().Be(first, "the acta names whoever's close actually landed");
        stored.ResultJson.Should().NotBeNull();
    }

    [HumansFact]
    public async Task OpenWithRosterAsync_WithARosterBuiltBeforeADraftEdit_WritesNothing()
    {
        var vote = await _fx.AddVoteAsync(
            status: AssemblyVoteStatus.Draft, kind: AssemblyVoteKind.YesNo);

        var stale = await _fx.Repository.GetByIdAsync(
            vote.Id, Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        // A Board edit lands while the roster is being built from that read.
        _fx.Clock.AdvanceMinutes(1);
        (await _fx.Service.UpdateDraftAsync(
                vote.Id,
                _fx.DraftFor(vote, AssemblyVoteKind.YesNo, []),
                Guid.NewGuid(),
                Xunit.TestContext.Current.CancellationToken))
            .Should().Be(AssemblyVoteActionResult.Ok);
        _fx.Db.ChangeTracker.Clear();

        stale!.Status = AssemblyVoteStatus.Open;
        stale.OpenedAt = _fx.Clock.GetCurrentInstant();
        stale.OpenedByUserId = Guid.NewGuid();

        var written = await _fx.Repository.OpenWithRosterAsync(
            stale,
            [new AssemblyVoteRoster
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                UserId = Guid.NewGuid(),
                IsOfficial = true
            }],
            Xunit.TestContext.Current.CancellationToken);

        written.Should().BeFalse();
        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.Status.Should().Be(AssemblyVoteStatus.Draft,
            "the electorate would have been computed from content the vote no longer has");
        (await _fx.Db.AssemblyVoteRosterEntries.AsNoTracking()
            .AnyAsync(r => r.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [HumansFact]
    public async Task GetVotesForMemberAsync_FinishesACloseThatNeverStoredItsResult()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(2));

        // A close that got its status down and then died: no result, no audit entry.
        var closing = await _fx.Db.AssemblyVotes
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        closing.Status = AssemblyVoteStatus.Closed;
        closing.ClosedAt = closing.ClosesAt;
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();
        _fx.Clock.AdvanceMinutes(10);

        // A list read, not a read of that vote's own page.
        await _fx.Service.GetVotesForMemberAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ResultJson.Should().NotBeNull(
            "an interrupted close must not wait for somebody to open that one vote");
        await _fx.Audit.Received(1).LogAsync(
            AuditAction.AssemblyVoteClosed, AuditEntityTypes.AssemblyVote, vote.Id,
            Arg.Any<string>(), AssemblyVoteService.LapseJobName);
    }

    [HumansFact]
    public async Task RunLapseAndReminderSweepAsync_FinishesACloseThatNeverStoredItsResult()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(2));

        var closing = await _fx.Db.AssemblyVotes
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        closing.Status = AssemblyVoteStatus.Closed;
        closing.ClosedAt = closing.ClosesAt;
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();
        _fx.Clock.AdvanceMinutes(10);

        await _fx.Service.RunLapseAndReminderSweepAsync(Xunit.TestContext.Current.CancellationToken);

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ResultJson.Should().NotBeNull(
            "the sweep queries Open votes, and this one is Closed with its tally still owed");
    }

    [HumansFact]
    public async Task Read_WhileACloseIsStillFinishing_DoesNotReplayIt()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(2));

        // A close whose status write has just committed: its own tail — the audit entry, the
        // notification, the tally — is running in another request right now.
        var closing = await _fx.Db.AssemblyVotes
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        closing.Status = AssemblyVoteStatus.Closed;
        closing.ClosedAt = closing.ClosesAt;
        closing.UpdatedAt = _fx.Clock.GetCurrentInstant();
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        await _fx.Service.GetVotesForMemberAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var stored = await _fx.Db.AssemblyVotes.AsNoTracking()
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ResultJson.Should().BeNull(
            "the close is running, not interrupted; the request that owns it stores the tally");
        await _fx.Audit.DidNotReceive().LogAsync(
            AuditAction.AssemblyVoteClosed, AuditEntityTypes.AssemblyVote, vote.Id,
            Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task PeekAsync_OnAVoteThatClosedWhileTheRequestWasRunning_RecordsNoPeek()
    {
        var vote = await _fx.AddVoteAsync(
            closesAt: _fx.Clock.GetCurrentInstant() + Duration.FromHours(6));

        // Straight at the repository: the service settles first, so the only way to reach the
        // peek write on a closed row is the race this guards.
        (await _fx.Service.StopAsync(
                vote.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken))
            .Should().Be(AssemblyVoteActionResult.Ok);
        _fx.Db.ChangeTracker.Clear();

        var recorded = await _fx.Repository.AddPeekAsync(
            new AssemblyVotePeek
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                AdminUserId = Guid.NewGuid(),
                PeekedAt = _fx.Clock.GetCurrentInstant()
            },
            Xunit.TestContext.Current.CancellationToken);

        recorded.Should().BeFalse();
        (await _fx.Db.AssemblyVotePeeks.AsNoTracking()
            .AnyAsync(p => p.VoteId == vote.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse("the results page publishes peeks, and nobody looked early");
    }

    [HumansFact]
    public async Task Read_WhenTheLapseCloseLosesToAnExtend_ReturnsTheVoteAsStillOpen()
    {
        var closesAt = _fx.Clock.GetCurrentInstant() + Duration.FromHours(1);
        var vote = await _fx.AddVoteAsync(closesAt: closesAt);
        var member = Guid.NewGuid();
        await _fx.AddRosterRowAsync(vote.Id, member, isOfficial: true);

        // Past the deadline this read knows about, but an Extend has already moved it.
        _fx.Clock.AdvanceMinutes(90);
        var extended = _fx.Clock.GetCurrentInstant() + Duration.FromHours(5);
        var stored = await _fx.Db.AssemblyVotes
            .SingleAsync(v => v.Id == vote.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ClosesAt = extended;
        await _fx.Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _fx.Db.ChangeTracker.Clear();

        var outcome = await _fx.Service.CastBallotAsync(
            vote.Id, member, AssemblyBallotChoice.Yes, null,
            Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(BallotSubmissionOutcome.Recorded,
            "the vote is open until the extended deadline, whatever this request read first");
    }
}
