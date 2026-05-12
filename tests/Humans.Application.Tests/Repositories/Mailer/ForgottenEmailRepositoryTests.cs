using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Mailer;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories.Mailer;

public sealed class ForgottenEmailRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ForgottenEmailRepository _repo;

    public ForgottenEmailRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new ForgottenEmailRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task AddManyAsync_IsIdempotent_OnUserIdEmailHash()
    {
        var userId = Guid.NewGuid();
        var hashes = new[] { "hashA", "hashB" };
        var at = Instant.FromUtc(2026, 5, 12, 12, 0);

        var inserted1 = await _repo.AddManyAsync(userId, hashes, at);
        var inserted2 = await _repo.AddManyAsync(userId, hashes, at);

        inserted1.Should().Be(2);
        inserted2.Should().Be(0);
    }

    [HumansFact]
    public async Task ExistsByHashAsync_ReturnsTrue_WhenHashMatches()
    {
        await _repo.AddManyAsync(Guid.NewGuid(), new[] { "matchme" }, Instant.MinValue);

        (await _repo.ExistsByHashAsync("matchme")).Should().BeTrue();
        (await _repo.ExistsByHashAsync("nope")).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetExistingHashesAsync_ReturnsOnlyPresentHashes()
    {
        await _repo.AddManyAsync(Guid.NewGuid(), new[] { "aaa", "bbb" }, Instant.MinValue);

        var result = await _repo.GetExistingHashesAsync(new[] { "aaa", "ccc" });

        result.Should().BeEquivalentTo(new[] { "aaa" });
    }

    [HumansFact]
    public async Task GetExistingHashesAsync_ReturnsEmpty_WhenInputEmpty()
    {
        var result = await _repo.GetExistingHashesAsync(Array.Empty<string>());
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddManyAsync_ReturnsZero_WhenInputEmpty()
    {
        var count = await _repo.AddManyAsync(Guid.NewGuid(), Array.Empty<string>(), Instant.MinValue);
        count.Should().Be(0);
    }

    [HumansFact]
    public async Task CountAsync_ReturnsTotal()
    {
        await _repo.AddManyAsync(Guid.NewGuid(), new[] { "x", "y", "z" }, Instant.MinValue);

        var count = await _repo.CountAsync();
        count.Should().Be(3);
    }
}
