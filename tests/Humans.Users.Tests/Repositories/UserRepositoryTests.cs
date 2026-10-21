using AwesomeAssertions;
using Xunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Users.Data;

namespace Humans.Users.Tests.Repositories;

public sealed class UserRepositoryTests : IDisposable
{
    private readonly UsersDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly UserRepository _repo;

    public UserRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new UsersDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new UserRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private async Task<User> SeedUserAsync(string? googleEmail = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"user-{Guid.NewGuid():N}@example.com",
            Email = $"user-{Guid.NewGuid():N}@example.com",
            DisplayName = "Seeded User",
            BurnerName = "Seeded User",
            CreatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.Users.Add(user);
        if (googleEmail is not null)
        {
            _dbContext.Entry(user).Property<string?>("GoogleEmail").CurrentValue = googleEmail;
        }
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return user;
    }

    // ==========================================================================
    // SetLastLoginAsync (A1 — single owner of the login stamp)
    // ==========================================================================

    [HumansFact]
    public async Task SetLastLoginAsync_StampsLastLoginAt_AndReturnsTrue()
    {
        var user = await SeedUserAsync();
        var at = _clock.GetCurrentInstant();

        var result = await _repo.SetLastLoginAsync(user.Id, at, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Users.SingleAsync(u => u.Id == user.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.LastLoginAt.Should().Be(at);
    }

    [HumansFact]
    public async Task SetLastLoginAsync_ReturnsFalse_WhenUserDoesNotExist()
    {
        var result = await _repo.SetLastLoginAsync(
            Guid.NewGuid(), _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // SetDeletionPendingAsync / ClearDeletionAsync
    // ==========================================================================

    [HumansFact]
    public async Task SetDeletionPendingAsync_SetsBothFieldsAndReturnsTrue()
    {
        var user = await SeedUserAsync();
        var requested = Instant.FromUtc(2026, 4, 1, 0, 0);
        var scheduled = Instant.FromUtc(2026, 5, 1, 0, 0);

        var result = await _repo.SetDeletionPendingAsync(user.Id, requested, scheduled, null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.DeletionRequestedAt.Should().Be(requested);
        reloaded.DeletionScheduledFor.Should().Be(scheduled);
    }

    [HumansFact]
    public async Task ClearDeletionAsync_ClearsAllDeletionFieldsIncludingEligibleAfter()
    {
        var user = await SeedUserAsync();
        user.DeletionRequestedAt = Instant.FromUtc(2026, 4, 1, 0, 0);
        user.DeletionScheduledFor = Instant.FromUtc(2026, 5, 1, 0, 0);
        user.DeletionEligibleAfter = Instant.FromUtc(2026, 6, 1, 0, 0);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.ClearDeletionAsync(user.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.DeletionRequestedAt.Should().BeNull();
        reloaded.DeletionScheduledFor.Should().BeNull();
        reloaded.DeletionEligibleAfter.Should().BeNull();
    }

    // ==========================================================================
    // ApplyExpiredDeletionAnonymizationAsync — deletes AspNetUserLogins
    // ==========================================================================

    [HumansFact]
    public async Task ApplyExpiredDeletionAnonymizationAsync_RemovesAspNetUserLoginsForUser()
    {
        var user = await SeedUserAsync();
        var other = await SeedUserAsync();
        AddLogin(user.Id, "Google", "fred-google-sub");
        AddLogin(user.Id, "Microsoft", "fred-ms-sub");
        AddLogin(other.Id, "Google", "other-google-sub");
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.ApplyExpiredDeletionAnonymizationAsync(user.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var remaining = await _dbContext.Set<IdentityUserLogin<Guid>>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle()
            .Which.UserId.Should().Be(other.Id);
    }

    [HumansFact]
    public async Task ApplyExpiredDeletionAnonymizationAsync_NoLogins_StillAnonymizesUser()
    {
        var user = await SeedUserAsync();

        var result = await _repo.ApplyExpiredDeletionAnonymizationAsync(user.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.OriginalBurnerName.Should().Be("Seeded User");
    }

    [HumansFact]
    public async Task ApplyExpiredDeletionAnonymizationAsync_ScrubsLegacyIdentityEmailPii()
    {
        var user = await SeedUserAsync();
        var originalEmail = user.IdentityEmailColumn;
        originalEmail.Should().NotBeNullOrEmpty();

        var result = await _repo.ApplyExpiredDeletionAnonymizationAsync(user.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.IdentityEmailColumn.Should().Be($"deleted-{user.Id:N}@deleted.local",
            "the legacy Identity email column is scrubbed to a no-PII sentinel on deletion");
        reloaded.IdentityEmailColumn.Should().NotBe(originalEmail);
        reloaded.UserName.Should().Be($"deleted-{user.Id:N}@deleted.local");
    }

    [HumansFact]
    public async Task AnonymizeForMergeAsync_ScrubsLegacyIdentityEmailPii()
    {
        var source = await SeedUserAsync();
        var target = await SeedUserAsync();
        var originalEmail = source.IdentityEmailColumn;
        originalEmail.Should().NotBeNullOrEmpty();
        var now = _clock.GetCurrentInstant();

        var result = await _repo.AnonymizeForMergeAsync(source.Id, target.Id, now, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == source.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.IdentityEmailColumn.Should().Be($"merged-{source.Id:N}@merged.local",
            "the legacy Identity email column is scrubbed to a no-PII sentinel on the merge tombstone");
        reloaded.IdentityEmailColumn.Should().NotBe(originalEmail);
        reloaded.UserName.Should().Be($"merged-{source.Id:N}@merged.local");
        reloaded.MergedToUserId.Should().Be(target.Id);
        reloaded.MergedAt.Should().Be(now);
    }

    [HumansFact]
    public async Task AnonymizeForMergeAsync_LocksOutTheSourceFarFuture()
    {
        var source = await SeedUserAsync();
        var target = await SeedUserAsync();

        await _repo.AnonymizeForMergeAsync(source.Id, target.Id, _clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == source.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.LockoutEnabled.Should().BeTrue("a merge tombstone cannot sign in");
        reloaded.LockoutEnd.Should().Be(DateTimeOffset.MaxValue);
    }

    private void AddLogin(Guid userId, string loginProvider, string providerKey)
    {
        _dbContext.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
        {
            UserId = userId,
            LoginProvider = loginProvider,
            ProviderKey = providerKey,
            ProviderDisplayName = loginProvider,
        });
    }
}
