using AwesomeAssertions;
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
/// Service-enforced invariant tests for PR 3 of the email-identity-decoupling
/// spec. <c>SetProviderAsync</c> is the OAuth-callback hook for tagging a
/// UserEmail row with its OAuth identity; the service enforces single-row-per-
/// (Provider, ProviderKey) by clearing any sibling row holding the same pair
/// in the same write batch (no DB unique index per
/// feedback_db_enforcement_minimal). <c>FindByProviderKeyAsync</c> is the
/// rename-detection lookup.
/// </summary>
public class UserEmailServiceProviderInvariantsTests
{
    private readonly IUserEmailRepository _repository = Substitute.For<IUserEmailRepository>();
    private readonly IAccountMergeService _mergeService = Substitute.For<IAccountMergeService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly UserManager<User> _userManager;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 30, 12, 0));
    private readonly IFullProfileInvalidator _fullProfileInvalidator = Substitute.For<IFullProfileInvalidator>();
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
            _serviceProvider,
            NullLogger<UserEmailService>.Instance);
    }

    [HumansFact]
    public async Task SetProviderAsync_SetsPairOnTargetRow()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "user@example.com",
            IsVerified = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.FindAllByProviderKeyAsync("Google", "sub-A", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmail>());

        await _service.SetProviderAsync(userId, rowId, "Google", "sub-A");

        row.Provider.Should().Be("Google");
        row.ProviderKey.Should().Be("sub-A");
        row.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        await _repository.Received(1).UpdateBatchAsync(
            Arg.Is<IReadOnlyList<UserEmail>>(rows => rows.Count == 1 && rows[0].Id == rowId),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetProviderAsync_ClearsProviderOnConflictingRow()
    {
        var userId = Guid.NewGuid();
        var rowAId = Guid.NewGuid();
        var rowBId = Guid.NewGuid();
        var rowA = new UserEmail
        {
            Id = rowAId,
            UserId = userId,
            Email = "old@example.com",
            IsVerified = true,
            Provider = "Google",
            ProviderKey = "sub-A",
        };
        var rowB = new UserEmail
        {
            Id = rowBId,
            UserId = userId,
            Email = "new@example.com",
            IsVerified = true,
        };
        _repository.GetByIdAndUserIdAsync(rowBId, userId, Arg.Any<CancellationToken>())
            .Returns(rowB);
        _repository.FindAllByProviderKeyAsync("Google", "sub-A", Arg.Any<CancellationToken>())
            .Returns(new[] { rowA });

        await _service.SetProviderAsync(userId, rowBId, "Google", "sub-A");

        rowA.Provider.Should().BeNull();
        rowA.ProviderKey.Should().BeNull();
        rowB.Provider.Should().Be("Google");
        rowB.ProviderKey.Should().Be("sub-A");
        await _repository.Received(1).UpdateBatchAsync(
            Arg.Is<IReadOnlyList<UserEmail>>(rows =>
                rows.Count == 2
                && rows.Any(r => r.Id == rowAId && r.Provider == null)
                && rows.Any(r => r.Id == rowBId && r.Provider == "Google")),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetProviderAsync_OnNonexistentRow_Throws()
    {
        var userId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        _repository.GetByIdAndUserIdAsync(missingId, userId, Arg.Any<CancellationToken>())
            .Returns((UserEmail?)null);

        var act = async () => await _service.SetProviderAsync(userId, missingId, "Google", "sub-X");

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
        await _repository.DidNotReceive().UpdateBatchAsync(
            Arg.Any<IReadOnlyList<UserEmail>>(), Arg.Any<CancellationToken>());
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
