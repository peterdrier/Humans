using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Users;
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
/// Unit tests for the one-shot <see cref="UserEmailProviderBackfillService"/>
/// admin button (PR 3 of the email-identity-decoupling spec). The button is
/// idempotent — re-runs are safe — and audit-logs each row updated.
/// </summary>
public class UserEmailProviderBackfillServiceTests
{
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly IUserEmailRepository _userEmailRepository = Substitute.For<IUserEmailRepository>();
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 30, 12, 0));
    private readonly UserEmailProviderBackfillService _service;

    public UserEmailProviderBackfillServiceTests()
    {
        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        _service = new UserEmailProviderBackfillService(
            _userRepository,
            _userEmailRepository,
            _userManager,
            _auditLogService,
            _clock,
            NullLogger<UserEmailProviderBackfillService>.Instance);
    }

    [HumansFact]
    public async Task RunAsync_PopulatesProviderProviderKey_FromAspNetUserLogins()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "user@example.com" };
        var email = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "user@example.com",
            IsVerified = true,
        };
        _userRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { user });
        _userRepository.GetLegacyGoogleEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        _userEmailRepository.GetLegacyBackfillSnapshotsByUserIdAsync(
                userId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Humans.Application.DTOs.UserEmailLegacyBackfillSnapshot(
                    emailId, userId, "user@example.com", true, null, null, false, false),
            });
        _userEmailRepository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new[] { email });
        _userManager.GetLoginsAsync(user)
            .Returns(new List<UserLoginInfo> { new("Google", "sub-A", "Google") });

        var result = await _service.RunAsync();

        result.UsersProcessed.Should().Be(1);
        result.ProviderRowsUpdated.Should().Be(1);
        email.Provider.Should().Be("Google");
        email.ProviderKey.Should().Be("sub-A");
        await _userEmailRepository.Received().UpdateBatchAsync(
            Arg.Is<IReadOnlyList<UserEmail>>(rows => rows.Count >= 1 && rows.Any(r => r.Id == emailId)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RunAsync_SetsIsGoogle_OnRowMatchingLegacyGoogleEmail()
    {
        var userId = Guid.NewGuid();
        var googleRowId = Guid.NewGuid();
        var otherRowId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "personal@example.com",
        };
        var googleRow = new UserEmail
        {
            Id = googleRowId,
            UserId = userId,
            Email = "user@nobodies.team",
            IsVerified = true,
        };
        var otherRow = new UserEmail
        {
            Id = otherRowId,
            UserId = userId,
            Email = "personal@example.com",
            IsVerified = true,
        };
        _userRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { user });
        _userRepository.GetLegacyGoogleEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "user@nobodies.team" });
        _userEmailRepository.GetLegacyBackfillSnapshotsByUserIdAsync(
                userId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Humans.Application.DTOs.UserEmailLegacyBackfillSnapshot(
                    googleRowId, userId, "user@nobodies.team", true, null, null, false, false),
                new Humans.Application.DTOs.UserEmailLegacyBackfillSnapshot(
                    otherRowId, userId, "personal@example.com", true, null, null, false, false),
            });
        _userEmailRepository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new[] { googleRow, otherRow });
        _userManager.GetLoginsAsync(user).Returns(new List<UserLoginInfo>());

        var result = await _service.RunAsync();

        result.IsGoogleRowsUpdated.Should().Be(1);
        googleRow.IsGoogle.Should().BeTrue();
        otherRow.IsGoogle.Should().BeFalse();
    }

    [HumansFact]
    public async Task RunAsync_SetsIsGoogle_OnLegacyIsOAuthRow_WhenGoogleEmailNull()
    {
        var userId = Guid.NewGuid();
        var oauthRowId = Guid.NewGuid();
        var otherRowId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "old@example.com" };
        var oauthRow = new UserEmail
        {
            Id = oauthRowId,
            UserId = userId,
            Email = "user@gmail.com",
            IsVerified = true,
        };
        var otherRow = new UserEmail
        {
            Id = otherRowId,
            UserId = userId,
            Email = "secondary@example.com",
            IsVerified = true,
        };
        _userRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { user });
        _userRepository.GetLegacyGoogleEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        _userEmailRepository.GetLegacyBackfillSnapshotsByUserIdAsync(
                userId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Humans.Application.DTOs.UserEmailLegacyBackfillSnapshot(
                    oauthRowId, userId, "user@gmail.com", true, null, null, false, true),
                new Humans.Application.DTOs.UserEmailLegacyBackfillSnapshot(
                    otherRowId, userId, "secondary@example.com", true, null, null, false, false),
            });
        _userEmailRepository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new[] { oauthRow, otherRow });
        _userManager.GetLoginsAsync(user).Returns(new List<UserLoginInfo>());

        var result = await _service.RunAsync();

        result.IsGoogleRowsUpdated.Should().Be(1);
        oauthRow.IsGoogle.Should().BeTrue();
        otherRow.IsGoogle.Should().BeFalse();
    }

    [HumansFact]
    public async Task RunAsync_IsIdempotent_SecondRunUpdatesNothing()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "user@example.com" };
        var email = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "user@example.com",
            IsVerified = true,
            Provider = "Google",
            ProviderKey = "sub-A",
            IsGoogle = false,
        };
        _userRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { user });
        _userRepository.GetLegacyGoogleEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        _userEmailRepository.GetLegacyBackfillSnapshotsByUserIdAsync(
                userId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Humans.Application.DTOs.UserEmailLegacyBackfillSnapshot(
                    emailId, userId, "user@example.com", true, "Google", "sub-A", false, false),
            });
        _userEmailRepository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new[] { email });
        _userManager.GetLoginsAsync(user)
            .Returns(new List<UserLoginInfo> { new("Google", "sub-A", "Google") });

        var result = await _service.RunAsync();

        result.ProviderRowsUpdated.Should().Be(0);
        await _userEmailRepository.DidNotReceive().UpdateBatchAsync(
            Arg.Any<IReadOnlyList<UserEmail>>(), Arg.Any<CancellationToken>());
    }
}
