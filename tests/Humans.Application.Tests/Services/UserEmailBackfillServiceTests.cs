using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
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
/// Tests for the PR 1 email-identity-decoupling admin backfill service
/// (<c>docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md</c>).
/// </summary>
public class UserEmailBackfillServiceTests
{
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly IUserEmailRepository _userEmailRepository = Substitute.For<IUserEmailRepository>();
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IFullProfileInvalidator _fullProfileInvalidator = Substitute.For<IFullProfileInvalidator>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 28, 12, 0));
    private readonly UserEmailBackfillService _sut;

    public UserEmailBackfillServiceTests()
    {
        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        _sut = new UserEmailBackfillService(
            _userRepository,
            _userEmailRepository,
            _userManager,
            _auditLogService,
            _fullProfileInvalidator,
            _clock,
            NullLogger<UserEmailBackfillService>.Instance);
    }

    [HumansFact]
    public async Task NoOrphans_ReturnsZeroCounts()
    {
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User>());

        var result = await _sut.BackfillAsync();

        result.OrphansFound.Should().Be(0);
        result.RowsInserted.Should().Be(0);
        result.SkippedUserIds.Should().BeEmpty();
        await _userEmailRepository.DidNotReceive().AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SuccessfulInsert_InvalidatesFullProfileForThatUser()
    {
        // FullProfile.EmailAddresses is derived from user_emails — the cache
        // for the orphan user must be evicted after the insert so a follow-on
        // read sees the new address (per code-review-rules §Cache Invalidation).
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "x@example.com",
            EmailConfirmed = true,
        };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());

        await _sut.BackfillAsync();

        await _fullProfileInvalidator.Received(1).InvalidateAsync(user.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidationFailure_DoesNotAbortBatch_AndCountsAreAccurate()
    {
        // Per design-rules §7a: cache invalidation in a batch loop is
        // best-effort. A transient invalidation failure must not abort the
        // remaining inserts or corrupt the returned counts.
        var u1 = new User { Id = Guid.NewGuid(), Email = "a@example.com", EmailConfirmed = true };
        var u2 = new User { Id = Guid.NewGuid(), Email = "b@example.com", EmailConfirmed = true };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { u1, u2 });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());
        _fullProfileInvalidator.InvalidateAsync(u1.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("simulated cache failure")));

        var result = await _sut.BackfillAsync();

        result.OrphansFound.Should().Be(2);
        result.RowsInserted.Should().Be(2);
        await _userEmailRepository.Received(2).AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        // Second user's invalidation still attempted.
        await _fullProfileInvalidator.Received(1).InvalidateAsync(u2.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AuditFailure_DoesNotAbortBatch_AndCountsAreAccurate()
    {
        // Per design-rules §7a: audit calls are best-effort by doctrine.
        var u1 = new User { Id = Guid.NewGuid(), Email = "a@example.com", EmailConfirmed = true };
        var u2 = new User { Id = Guid.NewGuid(), Email = "b@example.com", EmailConfirmed = true };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { u1, u2 });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());
        _auditLogService
            .When(s => s.LogAsync(
                Arg.Any<Humans.Domain.Enums.AuditAction>(),
                nameof(User), u1.Id, Arg.Any<string>(), nameof(UserEmailBackfillService),
                Arg.Any<Guid?>(), Arg.Any<string?>()))
            .Do(_ => throw new InvalidOperationException("simulated audit failure"));

        var result = await _sut.BackfillAsync();

        result.OrphansFound.Should().Be(2);
        result.RowsInserted.Should().Be(2);
        await _userEmailRepository.Received(2).AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SkippedOrphan_DoesNotInvalidate()
    {
        // Skipped users (no User.Email) had no UserEmail row inserted, so
        // there's nothing to invalidate.
        var user = new User { Id = Guid.NewGuid(), Email = null };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user });

        await _sut.BackfillAsync();

        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task OneOrphanWithVerifiedEmail_InsertsOneRow()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "x@example.com",
            EmailConfirmed = true,
        };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());

        var result = await _sut.BackfillAsync();

        result.OrphansFound.Should().Be(1);
        result.RowsInserted.Should().Be(1);
        result.SkippedUserIds.Should().BeEmpty();

        await _userEmailRepository.Received(1).AddAsync(
            Arg.Is<UserEmail>(ue =>
                ue.UserId == user.Id &&
                ue.Email == "x@example.com" &&
                ue.IsVerified == true &&
                ue.IsNotificationTarget == true &&
                ue.IsOAuth == false),
            Arg.Any<CancellationToken>());

        await _auditLogService.Received(1).LogAsync(
            Arg.Any<Humans.Domain.Enums.AuditAction>(),
            nameof(User),
            user.Id,
            Arg.Any<string>(),
            nameof(UserEmailBackfillService));
    }

    [HumansFact]
    public async Task OrphanWithEmailAndOAuthLogin_SetsIsOAuthTrue()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "google@example.com",
            EmailConfirmed = true,
        };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user });
        _userManager.GetLoginsAsync(user)
            .Returns(new List<UserLoginInfo> { new("Google", "sub-123", "Google") });

        await _sut.BackfillAsync();

        await _userEmailRepository.Received(1).AddAsync(
            Arg.Is<UserEmail>(ue => ue.IsOAuth == true),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task OrphanWithoutEmail_RecordsSkip_NoRowInserted()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = null,
            EmailConfirmed = false,
        };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user });

        var result = await _sut.BackfillAsync();

        result.OrphansFound.Should().Be(1);
        result.RowsInserted.Should().Be(0);
        result.SkippedUserIds.Should().ContainSingle().Which.Should().Be(user.Id);
        await _userEmailRepository.DidNotReceive().AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<Humans.Domain.Enums.AuditAction>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UnverifiedEmail_PreservesIsVerifiedFlagAndDoesNotSetNotificationTarget()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "unconfirmed@example.com",
            EmailConfirmed = false,
        };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());

        await _sut.BackfillAsync();

        await _userEmailRepository.Received(1).AddAsync(
            Arg.Is<UserEmail>(ue =>
                ue.IsVerified == false &&
                ue.IsNotificationTarget == false),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Idempotent_SecondRunSeesNoOrphans()
    {
        // First call returns one orphan; the GetUsersWithoutUserEmailRowAsync
        // mock returns it. Once the test code "runs" once, the subsequent
        // call (set up with .Returns(empty)) reflects the persisted insert.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "x@example.com",
            EmailConfirmed = true,
        };
        _userRepository.GetUsersWithoutUserEmailRowAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { user }, new List<User>());
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());

        var first = await _sut.BackfillAsync();
        var second = await _sut.BackfillAsync();

        first.RowsInserted.Should().Be(1);
        second.OrphansFound.Should().Be(0);
        second.RowsInserted.Should().Be(0);
    }
}
