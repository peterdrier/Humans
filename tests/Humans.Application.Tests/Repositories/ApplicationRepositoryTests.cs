using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Xunit;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Tests.Repositories;

public sealed class ApplicationRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ApplicationRepository _repo;

    public ApplicationRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new ApplicationRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
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
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetByIdAsync(app.Id);

        result.Should().NotBeNull();
        result!.BoardVotes.Should().HaveCount(1);
        result.StateHistory.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsApplicationsOrderedBySubmittedAtDescending()
    {
        var userId = Guid.NewGuid();
        var older = SeedApp(userId, submittedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
        var newer = SeedApp(userId, submittedAt: Instant.FromUtc(2026, 3, 1, 0, 0));

        var result = await _repo.GetByUserIdAsync(userId);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(newer.Id);
        result[1].Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task GetByUserIdAsync_ExcludesOtherUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        SeedApp(userA);
        SeedApp(userB);

        var result = await _repo.GetByUserIdAsync(userA);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userA);
    }

    [Fact]
    public async Task AnySubmittedForUserAsync_ReturnsTrueOnlyForSubmittedStatus()
    {
        var userId = Guid.NewGuid();
        SeedApp(userId);

        (await _repo.AnySubmittedForUserAsync(userId)).Should().BeTrue();
        (await _repo.AnySubmittedForUserAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task AnySubmittedForUserAsync_DoesNotMatchApprovedOrWithdrawn()
    {
        var userId = Guid.NewGuid();
        var approved = SeedApp(userId);
        approved.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _dbContext.SaveChangesAsync();

        (await _repo.AnySubmittedForUserAsync(userId)).Should().BeFalse();
    }

    [Fact]
    public async Task CountByStatusAsync_CountsOnlyMatchingStatus()
    {
        SeedApp();
        SeedApp();

        (await _repo.CountByStatusAsync(ApplicationStatus.Submitted)).Should().Be(2);
        (await _repo.CountByStatusAsync(ApplicationStatus.Approved)).Should().Be(0);
    }

    [Fact]
    public async Task GetFilteredAsync_DefaultsToSubmitted()
    {
        var submitted = SeedApp();
        var approved = SeedApp();
        approved.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _dbContext.SaveChangesAsync();

        var (items, total) = await _repo.GetFilteredAsync(status: null, tier: null, page: 1, pageSize: 10);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].Id.Should().Be(submitted.Id);
    }

    [Fact]
    public async Task GetFilteredAsync_FiltersByStatus()
    {
        SeedApp();
        var approved = SeedApp();
        approved.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _dbContext.SaveChangesAsync();

        var (items, total) = await _repo.GetFilteredAsync(ApplicationStatus.Approved, null, 1, 10);

        total.Should().Be(1);
        items[0].Id.Should().Be(approved.Id);
    }

    [Fact]
    public async Task GetFilteredAsync_FiltersByTier()
    {
        SeedApp(tier: MembershipTier.Colaborador);
        SeedApp(tier: MembershipTier.Asociado);

        var (items, total) = await _repo.GetFilteredAsync(
            ApplicationStatus.Submitted, MembershipTier.Asociado, 1, 10);

        total.Should().Be(1);
        items[0].MembershipTier.Should().Be(MembershipTier.Asociado);
    }

    [Fact]
    public async Task GetFilteredAsync_Pagination()
    {
        for (var i = 0; i < 3; i++)
            SeedApp();

        var (items, total) = await _repo.GetFilteredAsync(
            ApplicationStatus.Submitted, null, page: 1, pageSize: 2);

        total.Should().Be(3);
        items.Should().HaveCount(2);
    }

    [Fact]
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

        await _repo.AddAsync(app);

        var reloaded = await _dbContext.Applications.FindAsync(app.Id);
        reloaded.Should().NotBeNull();
    }

    [Fact]
    public async Task FinalizeAsync_DeletesAllBoardVotesForApplication()
    {
        var app = SeedApp();
        await _dbContext.BoardVotes.AddRangeAsync(
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = Guid.NewGuid(), Vote = VoteChoice.Yay, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) },
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = Guid.NewGuid(), Vote = VoteChoice.No, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) });
        await _dbContext.SaveChangesAsync();

        app.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _repo.FinalizeAsync(app);

        var remaining = await _dbContext.BoardVotes.Where(bv => bv.ApplicationId == app.Id).ToListAsync();
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task FinalizeAsync_DoesNotDeleteVotesOnOtherApplications()
    {
        var appA = SeedApp();
        var appB = SeedApp();
        _dbContext.BoardVotes.Add(new BoardVote { Id = Guid.NewGuid(), ApplicationId = appB.Id, BoardMemberUserId = Guid.NewGuid(), Vote = VoteChoice.Yay, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) });
        await _dbContext.SaveChangesAsync();

        appA.Approve(Guid.NewGuid(), "ok", new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));
        await _repo.FinalizeAsync(appA);

        var otherVotes = await _dbContext.BoardVotes.Where(bv => bv.ApplicationId == appB.Id).ToListAsync();
        otherVotes.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetVoterIdsForApplicationAsync_ReturnsEveryVoterOnce()
    {
        var app = SeedApp();
        var voter1 = Guid.NewGuid();
        var voter2 = Guid.NewGuid();
        await _dbContext.BoardVotes.AddRangeAsync(
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = voter1, Vote = VoteChoice.Yay, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) },
            new BoardVote { Id = Guid.NewGuid(), ApplicationId = app.Id, BoardMemberUserId = voter2, Vote = VoteChoice.No, VotedAt = Instant.FromUtc(2026, 3, 1, 12, 0) });
        await _dbContext.SaveChangesAsync();

        var ids = await _repo.GetVoterIdsForApplicationAsync(app.Id);

        ids.Should().BeEquivalentTo(new[] { voter1, voter2 });
    }

    [Fact]
    public async Task GetVoterIdsForApplicationAsync_EmptyForNoVotes()
    {
        var app = SeedApp();

        var ids = await _repo.GetVoterIdsForApplicationAsync(app.Id);

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_PersistsMutations()
    {
        var app = SeedApp();
        app.Withdraw(new NodaTime.Testing.FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)));

        await _repo.UpdateAsync(app);

        var reloaded = await _dbContext.Applications.FindAsync(app.Id);
        reloaded!.Status.Should().Be(ApplicationStatus.Withdrawn);
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
