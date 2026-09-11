using AwesomeAssertions;
using Humans.AuditLog.Contracts;
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
}
