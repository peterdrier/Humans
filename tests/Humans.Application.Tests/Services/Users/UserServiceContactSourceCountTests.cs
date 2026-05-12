using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Profiles;
using Humans.Infrastructure.Repositories.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Users;

public sealed class UserServiceContactSourceCountTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IUserService _service;

    public UserServiceContactSourceCountTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        var factory = new TestDbContextFactory(options);

        var userRepo = new UserRepository(factory);
        var userEmailRepo = new UserEmailRepository(factory);

        _service = new UserService(
            userRepo,
            userEmailRepo,
            Substitute.For<IFullProfileInvalidator>(),
            Substitute.For<IAdminAuthorizationService>(),
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<UserService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedUserAsync(ContactSource? source)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"user-{Guid.NewGuid():N}@example.com",
            Email = $"user-{Guid.NewGuid():N}@example.com",
            DisplayName = "Test User",
            ContactSource = source,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
    }

    [HumansFact]
    public async Task GetCountByContactSourceAsync_CountsOnlyMatchingContactSource()
    {
        // Seed: 3 MailerLite, 1 TicketTailor, 2 nulls.
        await SeedUserAsync(ContactSource.MailerLite);
        await SeedUserAsync(ContactSource.MailerLite);
        await SeedUserAsync(ContactSource.MailerLite);
        await SeedUserAsync(ContactSource.TicketTailor);
        await SeedUserAsync(null);
        await SeedUserAsync(null);

        var count = await _service.GetCountByContactSourceAsync(ContactSource.MailerLite);

        count.Should().Be(3);
    }

    [HumansFact]
    public async Task GetCountByContactSourceAsync_ReturnsZero_WhenNoUsersMatchSource()
    {
        await SeedUserAsync(ContactSource.TicketTailor);
        await SeedUserAsync(null);

        var count = await _service.GetCountByContactSourceAsync(ContactSource.MailerLite);

        count.Should().Be(0);
    }
}
