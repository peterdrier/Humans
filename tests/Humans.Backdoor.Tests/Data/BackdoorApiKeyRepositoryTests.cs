using AwesomeAssertions;
using Humans.Backdoor.Data;
using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Backdoor.Tests.Data;

public sealed class BackdoorApiKeyRepositoryTests : IDisposable
{
    private readonly BackdoorDbContext _db;
    private readonly BackdoorApiKeyRepository _repository;

    public BackdoorApiKeyRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<BackdoorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new BackdoorDbContext(options);
        _repository = new BackdoorApiKeyRepository(new TestDbContextFactory<BackdoorDbContext>(options));
    }

    public void Dispose() => _db.Dispose();

    [HumansFact]
    public async Task FindActiveByHashAsync_excludes_revoked_keys()
    {
        var active = Key("active");
        var revoked = Key("revoked");
        revoked.RevokedAt = Instant.FromUtc(2026, 9, 21, 6, 0);
        await _db.ApiKeys.AddRangeAsync([active, revoked], Xunit.TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        (await _repository.FindActiveByHashAsync(active.KeyHash, Xunit.TestContext.Current.CancellationToken))
            .Should().NotBeNull().And.Match<BackdoorApiKey>(key => key.Id == active.Id);
        (await _repository.FindActiveByHashAsync(revoked.KeyHash, Xunit.TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [HumansFact]
    public async Task RevokeAsync_stamps_once_and_is_idempotent()
    {
        var key = Key("revoke");
        var revokedBy = Guid.NewGuid();
        var revokedAt = Instant.FromUtc(2026, 9, 21, 6, 0);
        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        (await _repository.RevokeAsync(key.Id, revokedBy, revokedAt, Xunit.TestContext.Current.CancellationToken))
            .Should().BeTrue();
        (await _repository.RevokeAsync(key.Id, Guid.NewGuid(), revokedAt.Plus(Duration.FromHours(1)), Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse();

        var persisted = await _db.ApiKeys.AsNoTracking()
            .SingleAsync(item => item.Id == key.Id, Xunit.TestContext.Current.CancellationToken);
        persisted.RevokedAt.Should().Be(revokedAt);
        persisted.RevokedByUserId.Should().Be(revokedBy);
    }

    private static BackdoorApiKey Key(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        KeyHash = suffix,
        DisplayPrefix = suffix,
        Label = suffix,
        CreatedAt = Instant.FromUtc(2026, 9, 21, 5, 0),
        CreatedByUserId = Guid.NewGuid()
    };
}
