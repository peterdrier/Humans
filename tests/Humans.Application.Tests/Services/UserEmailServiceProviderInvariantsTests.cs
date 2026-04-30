using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profile;
using Humans.Domain.Entities;
using Humans.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Service-enforced invariant tests for the OAuth provider lookup. The
/// single-row-per-(Provider, ProviderKey) invariant is service-enforced by
/// <see cref="IUserEmailService.LinkAsync"/> (no DB unique index per
/// feedback_db_enforcement_minimal). <c>FindByProviderKeyAsync</c> is the
/// rename-detection lookup used by the OAuth callback.
/// </summary>
public class UserEmailServiceProviderInvariantsTests
{
    private readonly IUserEmailRepository _repository = Substitute.For<IUserEmailRepository>();
    private readonly IAccountMergeService _mergeService = Substitute.For<IAccountMergeService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly UserManager<User> _userManager;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 30, 12, 0));
    private readonly IFullProfileInvalidator _fullProfileInvalidator = Substitute.For<IFullProfileInvalidator>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly UserEmailService _service;

    public UserEmailServiceProviderInvariantsTests()
    {
        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);
        _serviceProvider.GetService(typeof(IAccountMergeService)).Returns(_mergeService);

        _service = new UserEmailService(
            _repository,
            _userService,
            _userManager,
            _clock,
            _fullProfileInvalidator,
            _auditLogService,
            _serviceProvider,
            NullLogger<UserEmailService>.Instance);
    }

    [HumansFact]
    public async Task FindByProviderKeyAsync_ReturnsRow_WhenSingleMatch()
    {
        var row = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Email = "user@example.com",
            IsVerified = true,
            Provider = "Google",
            ProviderKey = "sub-X",
        };
        _repository.FindAllByProviderKeyAsync("Google", "sub-X", Arg.Any<CancellationToken>())
            .Returns(new[] { row });

        var result = await _service.FindByProviderKeyAsync("Google", "sub-X");

        result.Should().NotBeNull();
        result!.Id.Should().Be(row.Id);
        result.UserId.Should().Be(row.UserId);
        result.Email.Should().Be(row.Email);
    }

    [HumansFact]
    public async Task FindByProviderKeyAsync_ReturnsNull_WhenNoMatch()
    {
        _repository.FindAllByProviderKeyAsync("Google", "sub-Y", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmail>());

        var result = await _service.FindByProviderKeyAsync("Google", "sub-Y");

        result.Should().BeNull();
    }
}
