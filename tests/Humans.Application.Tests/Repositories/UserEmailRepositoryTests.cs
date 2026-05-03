using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Repository tests for <see cref="UserEmailRepository"/> — PR 4 Task 4.
/// </summary>
public sealed class UserEmailRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly UserEmailRepository _repo;

    public UserEmailRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new UserEmailRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SetGoogleExclusiveAsync_FlipsExclusively()
    {
        var userId = Guid.NewGuid();
        var rowA = await SeedVerifiedAsync(userId, "a@x.test", isGoogle: true);
        var rowB = await SeedVerifiedAsync(userId, "b@x.test", isGoogle: false);
        var rowC = await SeedVerifiedAsync(userId, "c@x.test", isGoogle: false);

        var updatedAt = Instant.FromUtc(2026, 4, 30, 12, 0);
        await _repo.SetGoogleExclusiveAsync(userId, rowB.Id, updatedAt, default);

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
        await _dbContext.SaveChangesAsync();
        _dbContext.Entry(row).State = EntityState.Detached;
        return row;
    }

    private async Task<UserEmail?> GetByIdAsync(Guid id)
        => await _dbContext.UserEmails.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
}
