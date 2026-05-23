using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Repositories.Profiles;
using Humans.Infrastructure.Services.Profiles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using CommunicationPreferenceService = Humans.Application.Services.Profiles.CommunicationPreferenceService;

namespace Humans.Application.Tests.Services.Profiles;

public sealed class CommunicationPreferenceCountTests : ServiceTestHarness
{
    private readonly CommunicationPreferenceService _service;

    public CommunicationPreferenceCountTests()
        : base(Instant.FromUtc(2026, 5, 1, 12, 0))
    {
        var repository = new CommunicationPreferenceRepository(DbFactory);

        var dataProtectionProvider = DataProtectionProvider.Create("TestApp");
        var emailSettings = Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" });
        var tokenProvider = new UnsubscribeTokenProvider(
            dataProtectionProvider, emailSettings,
            NullLogger<UnsubscribeTokenProvider>.Instance);

        _service = new CommunicationPreferenceService(
            repository,
            Substitute.For<IUserService>(),
            tokenProvider,
            Clock,
            AuditLog,
            NullLogger<CommunicationPreferenceService>.Instance);
    }

    [HumansFact]
    public async Task GetCountByCategoryAndStateAsync_CountsOptedInAndOut()
    {
        var now = Clock.GetCurrentInstant();

        // Seed: 5 users with Marketing OptedOut=false, 3 with OptedOut=true.
        for (var i = 0; i < 5; i++)
        {
            Db.CommunicationPreferences.Add(new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Category = MessageCategory.Marketing,
                OptedOut = false,
                UpdatedAt = now,
                UpdateSource = "Test",
            });
        }

        for (var i = 0; i < 3; i++)
        {
            Db.CommunicationPreferences.Add(new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Category = MessageCategory.Marketing,
                OptedOut = true,
                UpdatedAt = now,
                UpdateSource = "Test",
            });
        }

        await Db.SaveChangesAsync();

        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: false))
            .Should().Be(5);
        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: true))
            .Should().Be(3);
    }
}
