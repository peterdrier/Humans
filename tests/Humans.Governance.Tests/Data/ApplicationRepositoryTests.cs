using Humans.Users.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using MemberApplication = Humans.Governance.Domain.Application;
using Humans.Governance.Data;

namespace Humans.Governance.Tests.Data;

public sealed class ApplicationRepositoryTests : IDisposable
{
    private readonly GovernanceDbContext _dbContext;
    private readonly ApplicationRepository _repo;
    private readonly DbContextOptions<GovernanceDbContext> _options;

    public ApplicationRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<GovernanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _options = options;
        _dbContext = new GovernanceDbContext(options);
        _repo = new ApplicationRepository(new TestDbContextFactory<GovernanceDbContext>(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [HumansFact]
    public async Task GetFilteredAsync_LargePageDoesNotWrapToEarlierApplications()
    {
        SeedApp();
        var first = await _repo.GetFilteredAsync(null, null, 1, 50, Xunit.TestContext.Current.CancellationToken);
        first.Items.Should().ContainSingle();
        var result = await _repo.GetFilteredAsync(null, null, int.MaxValue, 50, Xunit.TestContext.Current.CancellationToken);
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetByIdAsync_IncludesAggregateLocalNavs()
    {
        var app = SeedApp();
        _dbContext.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = app.Id,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.Yay,
            VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0)
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetByIdAsync(app.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.BoardVotes.Should().HaveCount(1);
        result.StateHistory.Should().NotBeNull();
    }

    [HumansFact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetByUserIdAsync_ReturnsAllApplicationsForUser()
    {
        // Display ordering (SubmittedAt desc) now lives in the controller per the
        // DisplaySortInControllers rule; the repository only scopes by user.
        var userId = Guid.NewGuid();
        var older = SeedApp(userId, submittedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
        var newer = SeedApp(userId, submittedAt: Instant.FromUtc(2026, 3, 1, 0, 0));

        var result = await _repo.GetByUserIdAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Select(a => a.Id).Should().BeEquivalentTo([older.Id, newer.Id]);
    }

    [HumansFact]
    public async Task GetByUserIdAsync_ExcludesOtherUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        SeedApp(userA);
        SeedApp(userB);

        var result = await _repo.GetByUserIdAsync(userA, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userA);
    }

    [HumansFact]
    public async Task AnySubmittedForUserAsync_MatchesOnlyTheApplicantsOwnRow()
    {
        var userId = Guid.NewGuid();
        SeedApp(userId);

        (await _repo.AnySubmittedForUserAsync(userId, Xunit.TestContext.Current.CancellationToken)).Should().BeTrue();
        (await _repo.AnySubmittedForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task AnySubmittedForUserAsync_DoesNotMatchAnApprovedApplication()
    {
        var userId = Guid.NewGuid();
        var approved = SeedApp(userId);
        approved.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        (await _repo.AnySubmittedForUserAsync(userId, Xunit.TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task CountByStatusAsync_CountsOnlyMatchingStatus()
    {
        SeedApp();
        SeedApp();

        (await _repo.CountByStatusAsync(ApplicationStatus.Submitted, Xunit.TestContext.Current.CancellationToken)).Should().Be(2);
        (await _repo.CountByStatusAsync(ApplicationStatus.Approved, Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task GetFilteredAsync_DefaultsToSubmitted()
    {
        var submitted = SeedApp();
        var approved = SeedApp();
        approved.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetFilteredAsync(status: null, tier: null, page: 1, pageSize: 10, ct: Xunit.TestContext.Current.CancellationToken);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].Id.Should().Be(submitted.Id);
    }

    [HumansFact]
    public async Task GetFilteredAsync_FiltersByStatus()
    {
        SeedApp();
        var approved = SeedApp();
        approved.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetFilteredAsync(ApplicationStatus.Approved, null, 1, 10, Xunit.TestContext.Current.CancellationToken);

        total.Should().Be(1);
        items[0].Id.Should().Be(approved.Id);
    }

    [HumansFact]
    public async Task GetFilteredAsync_FiltersByTier()
    {
        SeedApp(tier: MembershipTier.Colaborador);
        SeedApp(tier: MembershipTier.Asociado);

        var (items, total) = await _repo.GetFilteredAsync(
            ApplicationStatus.Submitted, MembershipTier.Asociado, 1, 10, Xunit.TestContext.Current.CancellationToken);

        total.Should().Be(1);
        items[0].MembershipTier.Should().Be(MembershipTier.Asociado);
    }

    [HumansFact]
    public async Task GetFilteredAsync_Pagination()
    {
        for (var i = 0; i < 3; i++)
            SeedApp();

        var (items, total) = await _repo.GetFilteredAsync(
            ApplicationStatus.Submitted, null, page: 1, pageSize: 2, ct: Xunit.TestContext.Current.CancellationToken);

        total.Should().Be(3);
        items.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task AddAsync_PersistsApplication()
    {
        var app = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "m",
            SubmittedAt = Instant.FromUtc(2026, 3, 1, 12, 0),
            UpdatedAt = Instant.FromUtc(2026, 3, 1, 12, 0)
        };

        await _repo.AddAsync(app, Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _dbContext.Applications.FindAsync(app.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.Should().NotBeNull();
    }

    [HumansFact]
    public async Task FinalizeAsync_DeletesAllBoardVotesForApplication()
    {
        var app = SeedApp();
        await _dbContext.BoardVotes.AddRangeAsync(
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = Guid.NewGuid(), Vote = VoteChoice.Yay, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) },
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = Guid.NewGuid(), Vote = VoteChoice.No, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        app.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _repo.FinalizeAsync(app, Xunit.TestContext.Current.CancellationToken);

        var remaining = await _dbContext.BoardVotes.Where(bv => bv.ApplicationId == app.Id).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ApplicationMutation_PreservesExistingChildrenAndAppendsTransitionHistory(bool finalize)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var app = SeedApp();
        app.StateHistory.Add(new ApplicationStateHistory
        {
            ApplicationId = app.Id,
            Status = ApplicationStatus.Submitted,
            ChangedByUserId = app.UserId,
            ChangedAt = app.SubmittedAt,
            Notes = "Original submission"
        });
        app.BoardVotes.Add(new BoardVote
        {
            ApplicationId = app.Id,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.Yay,
            VotedAt = app.SubmittedAt
        });
        await _dbContext.SaveChangesAsync(ct);

        var recorder = new ChildWriteRecorder();
        var options = new DbContextOptionsBuilder<GovernanceDbContext>(_options)
            .AddInterceptors(recorder).Options;
        var repo = new ApplicationRepository(new TestDbContextFactory<GovernanceDbContext>(options));
        var detached = (await repo.GetByIdAsync(app.Id, ct))!;
        var clock = new NodaTime.Testing.FakeClock(app.SubmittedAt + Duration.FromHours(1));
        if (finalize)
        {
            detached.Approve(Guid.NewGuid(), "Approved", clock);
            await repo.FinalizeAsync(detached, ct);
        }
        else
        {
            detached.Withdraw(clock);
            await repo.UpdateAsync(detached, ct);
        }

        var stored = (await repo.GetByIdAsync(app.Id, ct))!;
        stored.Status.Should().Be(finalize ? ApplicationStatus.Approved : ApplicationStatus.Withdrawn);
        stored.StateHistory.Should().HaveCount(2);
        stored.StateHistory.Should().ContainSingle(h => h.Notes == "Original submission");
        stored.StateHistory.Should().ContainSingle(h => h.ChangedAt == clock.GetCurrentInstant());
        stored.BoardVotes.Should().HaveCount(finalize ? 0 : 1);
        recorder.ModifiedChildren.Should().Be(0);
    }

    [HumansFact]
    public async Task FinalizeAsync_DoesNotDeleteVotesOnOtherApplications()
    {
        var appA = SeedApp();
        var appB = SeedApp();
        _dbContext.BoardVotes.Add(new BoardVote { Id = Guid.NewGuid(), ApplicationId = appB.Id, BoardMemberUserId = Guid.NewGuid(), Vote = VoteChoice.Yay, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        appA.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _repo.FinalizeAsync(appA, Xunit.TestContext.Current.CancellationToken);

        var otherVotes = await _dbContext.BoardVotes.Where(bv => bv.ApplicationId == appB.Id).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        otherVotes.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetVoterIdsForApplicationAsync_ReturnsEveryVoterOnce()
    {
        var app = SeedApp();
        var voter1 = Guid.NewGuid();
        var voter2 = Guid.NewGuid();
        await _dbContext.BoardVotes.AddRangeAsync(
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = voter1, Vote = VoteChoice.Yay, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) },
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = voter2, Vote = VoteChoice.No, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var ids = await _repo.GetVoterIdsForApplicationAsync(app.Id, Xunit.TestContext.Current.CancellationToken);

        ids.Should().BeEquivalentTo([voter1, voter2]);
    }

    [HumansFact]
    public async Task GetVoterIdsForApplicationAsync_EmptyForNoVotes()
    {
        var app = SeedApp();

        var ids = await _repo.GetVoterIdsForApplicationAsync(app.Id, Xunit.TestContext.Current.CancellationToken);

        ids.Should().BeEmpty();
    }

    [HumansFact]
    public async Task UpdateAsync_PersistsMutations()
    {
        var app = SeedApp();
        app.Withdraw(new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));

        await _repo.UpdateAsync(app, Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _dbContext.Applications.FindAsync(app.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded!.Status.Should().Be(ApplicationStatus.Withdrawn);
    }

    [HumansFact]
    public async Task ScrubFreeTextForUserAsync_removes_the_erased_persons_prose_and_keeps_governance_records()
    {
        var erasedUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var scrubbedAt = Instant.FromUtc(2026, 9, 21, 6, 0);
        var clock = new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        var ownApplication = SeedApp(erasedUserId);
        ownApplication.AdditionalInfo = "private context";
        ownApplication.SignificantContribution = "private contribution";
        ownApplication.RoleUnderstanding = "private understanding";
        ownApplication.DecisionNote = "private decision";
        // Seed legacy review prose directly; the former information-request workflow is gone.
        _dbContext.Entry(ownApplication).Property(application => application.ReviewNotes)
            .CurrentValue = "private state note";
        ownApplication.StateHistory.Add(new ApplicationStateHistory
        {
            ApplicationId = ownApplication.Id,
            Status = ApplicationStatus.Submitted,
            ChangedByUserId = erasedUserId,
            ChangedAt = clock.GetCurrentInstant(),
            Notes = "private state note"
        });

        var reviewedApplication = SeedApp(otherUserId);
        reviewedApplication.DecisionNote = "private reviewer decision";
        reviewedApplication.Approve(erasedUserId, "private reviewer note", clock);
        await _dbContext.BoardVotes.AddAsync(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = reviewedApplication.Id,
            BoardMemberUserId = erasedUserId,
            Vote = VoteChoice.Yay,
            Note = "private vote note",
            VotedAt = clock.GetCurrentInstant()
        }, Xunit.TestContext.Current.CancellationToken);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _repo.ScrubFreeTextForUserAsync(erasedUserId, scrubbedAt, Xunit.TestContext.Current.CancellationToken);

        _dbContext.ChangeTracker.Clear();
        var own = await _dbContext.Applications.Include(application => application.StateHistory)
            .SingleAsync(application => application.Id == ownApplication.Id, Xunit.TestContext.Current.CancellationToken);
        own.Motivation.Should().BeEmpty();
        own.AdditionalInfo.Should().BeNull();
        own.SignificantContribution.Should().BeNull();
        own.RoleUnderstanding.Should().BeNull();
        own.DecisionNote.Should().BeNull();
        own.ReviewNotes.Should().BeNull();
        own.UpdatedAt.Should().Be(scrubbedAt);
        own.UserId.Should().Be(erasedUserId);
        own.StateHistory.Should().AllSatisfy(history => history.Notes.Should().BeNull());

        var reviewed = await _dbContext.Applications.Include(application => application.StateHistory)
            .SingleAsync(application => application.Id == reviewedApplication.Id, Xunit.TestContext.Current.CancellationToken);
        reviewed.UserId.Should().Be(otherUserId);
        reviewed.Motivation.Should().Be("m");
        reviewed.DecisionNote.Should().BeNull();
        reviewed.ReviewNotes.Should().BeNull();
        reviewed.StateHistory.Should().AllSatisfy(history => history.Notes.Should().BeNull());

        var vote = await _dbContext.BoardVotes.SingleAsync(item => item.ApplicationId == reviewedApplication.Id,
            Xunit.TestContext.Current.CancellationToken);
        vote.Note.Should().BeNull();
        vote.UpdatedAt.Should().Be(scrubbedAt);
    }

    private sealed class ChildWriteRecorder : SaveChangesInterceptor
    {
        public int ModifiedChildren { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ModifiedChildren += eventData.Context!.ChangeTracker.Entries().Count(e =>
                e.State == EntityState.Modified && e.Entity is ApplicationStateHistory or BoardVote);
            return ValueTask.FromResult(result);
        }
    }

    private MemberApplication SeedApp(
        Guid? userId = null,
        Instant? submittedAt = null,
        MembershipTier tier = MembershipTier.Colaborador)
    {
        var app = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? Guid.NewGuid(),
            MembershipTier = tier,
            Motivation = "m",
            SubmittedAt = submittedAt ?? Instant.FromUtc(2026, 3, 1, 12, 0),
            UpdatedAt = submittedAt ?? Instant.FromUtc(2026, 3, 1, 12, 0)
        };
        _dbContext.Applications.Add(app);
        _dbContext.SaveChanges();
        return app;
    }
}
