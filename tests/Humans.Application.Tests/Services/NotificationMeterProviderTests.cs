using System.Security.Claims;
using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services;

public class NotificationMeterProviderTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly NotificationMeterProvider _provider;
    private readonly Instant _now = Instant.FromUtc(2026, 4, 3, 12, 0);

    public NotificationMeterProviderTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _provider = new NotificationMeterProvider(
            _dbContext,
            _cache,
            NullLogger<NotificationMeterProvider>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetMetersForUserAsync_Board_OnboardingMeterExcludesConsentReviewItems()
    {
        await SeedProfileAsync(consentCheckStatus: ConsentCheckStatus.Pending);
        await SeedProfileAsync();

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Board));

        var onboardingMeter = meters.Single(m =>
            string.Equals(m.Title, "Onboarding profiles pending", StringComparison.Ordinal));
        onboardingMeter.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetMetersForUserAsync_VolunteerCoordinator_SeesOnboardingMeter()
    {
        await SeedProfileAsync();

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.VolunteerCoordinator));

        meters.Should().ContainSingle(m =>
            string.Equals(m.Title, "Onboarding profiles pending", StringComparison.Ordinal) &&
            string.Equals(m.ActionUrl, "/OnboardingReview", StringComparison.Ordinal));
    }

    private async Task SeedProfileAsync(
        bool isApproved = false,
        bool isSuspended = false,
        ConsentCheckStatus? consentCheckStatus = null)
    {
        var userId = Guid.NewGuid();

        _dbContext.Users.Add(new User
        {
            Id = userId,
            UserName = $"{userId}@example.com",
            Email = $"{userId}@example.com",
            DisplayName = $"User {userId:N}",
            CreatedAt = _now,
        });

        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = _now,
            UpdatedAt = _now,
            IsApproved = isApproved,
            IsSuspended = isSuspended,
            ConsentCheckStatus = consentCheckStatus,
        });

        await _dbContext.SaveChangesAsync();
    }

    private static ClaimsPrincipal CreatePrincipal(params string[] roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }
}
