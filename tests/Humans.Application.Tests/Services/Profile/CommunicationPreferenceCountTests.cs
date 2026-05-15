using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Profiles;
using Humans.Infrastructure.Services.Profiles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using CommunicationPreferenceService = Humans.Application.Services.Profiles.CommunicationPreferenceService;

namespace Humans.Application.Tests.Services.Profiles;

public class CommunicationPreferenceCountTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly CommunicationPreferenceService _service;
    private readonly Instant _now = Instant.FromUtc(2026, 5, 1, 12, 0);

    public CommunicationPreferenceCountTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);

        var factory = new TestDbContextFactory(options);
        var repository = new CommunicationPreferenceRepository(factory);

        var dataProtectionProvider = DataProtectionProvider.Create("TestApp");
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" });
        var tokenProvider = new UnsubscribeTokenProvider(
            dataProtectionProvider, emailSettings,
            NullLogger<UnsubscribeTokenProvider>.Instance);

        var auditLog = Substitute.For<IAuditLogService>();
        var clock = new FakeClock(_now);

        _service = new CommunicationPreferenceService(
            repository,
            tokenProvider,
            clock,
            auditLog,
            NullLogger<CommunicationPreferenceService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task GetCountByCategoryAndStateAsync_CountsOptedInAndOut()
    {
        // Seed: 5 users with Marketing OptedOut=false, 3 with OptedOut=true.
        for (var i = 0; i < 5; i++)
        {
            _dbContext.CommunicationPreferences.Add(new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Category = MessageCategory.Marketing,
                OptedOut = false,
                UpdatedAt = _now,
                UpdateSource = "Test",
            });
        }

        for (var i = 0; i < 3; i++)
        {
            _dbContext.CommunicationPreferences.Add(new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Category = MessageCategory.Marketing,
                OptedOut = true,
                UpdatedAt = _now,
                UpdateSource = "Test",
            });
        }

        await _dbContext.SaveChangesAsync();

        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: false))
            .Should().Be(5);
        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: true))
            .Should().Be(3);
    }
}
