using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Repository tests for <see cref="UserRepository"/> — PR 4 Task 4.
/// </summary>
public sealed class UserRepositoryUserEmailTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly UserRepository _repo;

    public UserRepositoryUserEmailTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new UserRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [HumansFact]
    public async Task SetUserEmailGoogleExclusiveAsync_FlipsExclusively()
    {
        var userId = Guid.NewGuid();
        var rowA = await SeedVerifiedAsync(userId, "a@x.test", isGoogle: true);
        var rowB = await SeedVerifiedAsync(userId, "b@x.test", isGoogle: false);
        var rowC = await SeedVerifiedAsync(userId, "c@x.test", isGoogle: false);

        var updatedAt = Instant.FromUtc(2026, 4, 30, 12, 0);
        await _repo.SetUserEmailGoogleExclusiveAsync(userId, rowB.Id, updatedAt, Xunit.TestContext.Current.CancellationToken);

        var reloadedA = await GetByIdAsync(rowA.Id);
        var reloadedB = await GetByIdAsync(rowB.Id);
        var reloadedC = await GetByIdAsync(rowC.Id);

        reloadedA!.IsGoogle.Should().BeFalse();
        reloadedB!.IsGoogle.Should().BeTrue();
        reloadedC!.IsGoogle.Should().BeFalse();

        // UpdatedAt is bumped on rows whose IsGoogle value changed (A flipped
        // true→false, B flipped false→true). C's IsGoogle didn't change so its
        // UpdatedAt remains at the seed instant.
        reloadedA.UpdatedAt.Should().Be(updatedAt);
        reloadedB.UpdatedAt.Should().Be(updatedAt);
        reloadedC.UpdatedAt.Should().Be(SeedInstant);
    }

    // Note: the OAuth-callback write path is now driven by
    // UserEmailService.ReconcileOAuthIdentityAsync (issue
    // nobodies-collective/Humans#697); the legacy repo-level UpdateEmailAsync
    // primitive is gone. Service-level coverage lives in
    // UserEmailServiceReconcileOAuthTests; controller-level coverage in
    // AccountControllerOAuthReconcileTests.

    private static readonly Instant SeedInstant = Instant.FromUtc(2026, 3, 1, 12, 0);

    private async Task<UserEmail> SeedVerifiedAsync(Guid userId, string email, bool isGoogle)
    {
        var row = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = true,
            IsGoogle = isGoogle,
            IsPrimary = false,
            CreatedAt = SeedInstant,
            UpdatedAt = SeedInstant,
        };
        _dbContext.UserEmails.Add(row);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _dbContext.Entry(row).State = EntityState.Detached;
        return row;
    }

    private async Task<UserEmail?> GetByIdAsync(Guid id)
        => await _dbContext.UserEmails.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, Xunit.TestContext.Current.CancellationToken);
}
