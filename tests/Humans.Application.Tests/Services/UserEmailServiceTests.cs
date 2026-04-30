using AwesomeAssertions;
using System;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Profile;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Tests.Services;

public class UserEmailServiceTests
{
    private readonly IUserEmailRepository _repository = Substitute.For<IUserEmailRepository>();
    private readonly IAccountMergeService _mergeService = Substitute.For<IAccountMergeService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly UserManager<User> _userManager;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 21, 12, 0));
    private readonly IFullProfileInvalidator _fullProfileInvalidator = Substitute.For<IFullProfileInvalidator>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly UserEmailService _service;

    public UserEmailServiceTests()
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
    public async Task SetPrimaryAsync_VerifiedTarget_InvalidatesFullProfile()
    {
        var userId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var emails = new List<UserEmail>
        {
            new()
            {
                Id = targetId, UserId = userId, Email = "target@example.com",
                IsVerified = true, IsPrimary = false
            },
            new()
            {
                Id = otherId, UserId = userId, Email = "other@example.com",
                IsVerified = true, IsPrimary = true
            }
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(emails);

        await _service.SetPrimaryAsync(userId, targetId);

        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
        emails.Single(e => e.Id == targetId).IsPrimary.Should().BeTrue();
        emails.Single(e => e.Id == otherId).IsPrimary.Should().BeFalse();
    }

    [HumansFact]
    public async Task SetPrimaryAsync_UnverifiedTarget_DoesNotInvalidate()
    {
        var userId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var emails = new List<UserEmail>
        {
            new()
            {
                Id = targetId, UserId = userId, Email = "unverified@example.com",
                IsVerified = false, IsPrimary = false
            }
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(emails);

        var act = async () => await _service.SetPrimaryAsync(userId, targetId);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteEmailAsync_VerifiedSecondaryWithOtherAuthMethods_InvalidatesFullProfile()
    {
        // Verified non-OAuth secondary email + a remaining verified primary +
        // an OAuth login on AspNetUserLogins → preserve-auth-method invariant
        // is satisfied; delete proceeds and invalidates the FullProfile cache.
        var userId = Guid.NewGuid();
        var deletingId = Guid.NewGuid();
        var keepingId = Guid.NewGuid();
        var deleting = new UserEmail
        {
            Id = deletingId,
            UserId = userId,
            Email = "secondary@example.com",
            IsVerified = true,
            IsPrimary = false,
        };
        var keeping = new UserEmail
        {
            Id = keepingId,
            UserId = userId,
            Email = "primary@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.GetByIdAndUserIdAsync(deletingId, userId, Arg.Any<CancellationToken>())
            .Returns(deleting);
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { deleting, keeping });
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo> { new("Google", "sub-123", "Google") });

        await _service.DeleteEmailAsync(userId, deletingId);

        await _repository.Received(1).RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteEmailAsync_RejectsProviderAttachedRow()
    {
        // PR 4 service-level guard: Provider-attached rows MUST go through
        // UnlinkAsync (which removes both the AspNetUserLogins row and the
        // UserEmail row). The per-row UI never routes a Provider-attached row
        // to Delete; this test pins the service-level guard for non-UI callers.
        var userId = Guid.NewGuid();
        var providerRowId = Guid.NewGuid();
        var providerRow = new UserEmail
        {
            Id = providerRowId,
            UserId = userId,
            Email = "google@example.com",
            Provider = "Google",
            ProviderKey = "sub-Z",
            IsVerified = true,
            IsPrimary = false,
        };
        _repository.GetByIdAndUserIdAsync(providerRowId, userId, Arg.Any<CancellationToken>())
            .Returns(providerRow);

        var result = await _service.DeleteEmailAsync(userId, providerRowId);

        result.Should().BeFalse();
        await _repository.DidNotReceive().RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteEmailAsync_LastVerifiedEmailNoOAuthLogin_ThrowsValidationException()
    {
        // The user has one verified UserEmail and zero AspNetUserLogins. Deleting
        // the email would leave no auth method — block.
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var only = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "only@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.GetByIdAndUserIdAsync(emailId, userId, Arg.Any<CancellationToken>())
            .Returns(only);
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { only });
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());

        var act = async () => await _service.DeleteEmailAsync(userId, emailId);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
        await _repository.DidNotReceive().RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteEmailAsync_LastVerifiedEmailEvenWithOAuthLogin_ThrowsValidationException()
    {
        // Tightened from the original auth-method rule: even with an OAuth
        // login present, deleting the last verified email is blocked because
        // GetEffectiveEmail() falls back to User.Email which is null for
        // post-PR-1 users — the user would be un-notifiable. OAuth sign-in
        // still working isn't enough; the user must keep at least one
        // verified email so system mail (re-consent reminders, suspension
        // notices) has somewhere to go.
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var only = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "only@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.GetByIdAndUserIdAsync(emailId, userId, Arg.Any<CancellationToken>())
            .Returns(only);
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { only });

        var act = async () => await _service.DeleteEmailAsync(userId, emailId);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
        await _repository.DidNotReceive().RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteEmailAsync_UnverifiedEmail_AlwaysAllowed()
    {
        // Unverified emails are not auth methods (can't be used for magic-link
        // sign-in until verified). Deleting one bypasses the auth-method check
        // entirely.
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _repository.GetByIdAndUserIdAsync(emailId, userId, Arg.Any<CancellationToken>())
            .Returns(new UserEmail
            {
                Id = emailId,
                UserId = userId,
                Email = "unverified@example.com",
                IsVerified = false,
                IsPrimary = false,
            });

        await _service.DeleteEmailAsync(userId, emailId);

        await _repository.Received(1).RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        // GetLoginsAsync should not even be called since the verified branch is skipped.
        await _userManager.DidNotReceive().GetLoginsAsync(Arg.Any<User>());
    }

    [HumansFact]
    public async Task SetGoogleAsync_FlipsExclusively()
    {
        var userId = Guid.NewGuid();
        var rowAId = Guid.NewGuid();
        var rowBId = Guid.NewGuid();
        var rowA = new UserEmail
        {
            Id = rowAId, UserId = userId, Email = "a@x.test",
            IsVerified = true, IsGoogle = true,
        };
        var rowB = new UserEmail
        {
            Id = rowBId, UserId = userId, Email = "b@x.test",
            IsVerified = true, IsGoogle = false,
        };
        _repository.GetByIdAndUserIdAsync(rowBId, userId, Arg.Any<CancellationToken>())
            .Returns(rowB);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { rowA, rowB });
        _repository.SetGoogleExclusiveAsync(
            userId, rowBId, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ =>
            {
                rowA.IsGoogle = false;
                rowB.IsGoogle = true;
            });

        var result = await _service.SetGoogleAsync(userId, rowBId, userId);

        result.Should().BeTrue();
        rowA.IsGoogle.Should().BeFalse();
        rowB.IsGoogle.Should().BeTrue();
        await _repository.Received(1).SetGoogleExclusiveAsync(
            userId, rowBId, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetGoogleAsync_RejectsOtherUser()
    {
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        // Owner-gate: GetByIdAndUserIdAsync(rowId, otherId) returns null because
        // the row is owned by ownerId, not otherId.
        _repository.GetByIdAndUserIdAsync(rowId, otherId, Arg.Any<CancellationToken>())
            .Returns((UserEmail?)null);

        var result = await _service.SetGoogleAsync(otherId, rowId, otherId);

        result.Should().BeFalse();
        await _repository.DidNotReceive().SetGoogleExclusiveAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetGoogleAsync_RejectsUnverified()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId, UserId = userId, Email = "a@x.test",
            IsVerified = false, IsGoogle = false,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);

        var result = await _service.SetGoogleAsync(userId, rowId, userId);

        result.Should().BeFalse();
        row.IsGoogle.Should().BeFalse();
        await _repository.DidNotReceive().SetGoogleExclusiveAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LinkAsync_AttachesToExistingEmail()
    {
        // A row matching the email already exists for the user → attach
        // Provider/ProviderKey to that row instead of creating a new one.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var existing = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "match@example.com",
            IsVerified = true,
            IsPrimary = false,
        };
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { existing });
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _service.LinkAsync(
            userId, "Google", "sub-abc", "Match@Example.com", actorId);

        result.Should().BeTrue();
        existing.Provider.Should().Be("Google");
        existing.ProviderKey.Should().Be("sub-abc");
        await _repository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailLinked,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task LinkAsync_CreatesRowWhenMissing()
    {
        // No row matches the email → create a new verified, non-primary
        // UserEmail row with Provider/ProviderKey set.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());

        UserEmail? added = null;
        await _repository.AddAsync(
            Arg.Do<UserEmail>(e => added = e),
            Arg.Any<CancellationToken>());

        var result = await _service.LinkAsync(
            userId, "Google", "sub-xyz", "new@example.com", actorId);

        result.Should().BeTrue();
        added.Should().NotBeNull();
        added!.UserId.Should().Be(userId);
        added.Email.Should().Be("new@example.com");
        added.Provider.Should().Be("Google");
        added.ProviderKey.Should().Be("sub-xyz");
        added.IsVerified.Should().BeTrue();
        added.IsPrimary.Should().BeFalse();
        added.IsGoogle.Should().BeFalse();
        await _repository.Received(1).AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailLinked,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UnlinkAsync_RemovesAspNetUserLoginsAndEmailRow()
    {
        // Provider-attached row is removed from both the AspNetUserLogins table
        // (via UserManager.RemoveLoginAsync) and user_emails (via _repo.RemoveAsync).
        // FullProfile cache is invalidated and an audit log entry is written.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "linked@example.com",
            Provider = "Google",
            ProviderKey = "sub-Z",
            IsVerified = true,
        };
        var user = new User { Id = userId };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.RemoveLoginAsync(user, "Google", "sub-Z")
            .Returns(IdentityResult.Success);

        var result = await _service.UnlinkAsync(userId, rowId, actorId);

        result.Should().BeTrue();
        await _userManager.Received(1).RemoveLoginAsync(user, "Google", "sub-Z");
        await _repository.Received(1).RemoveAsync(row, Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailUnlinked,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UnlinkAsync_RejectsRowWithoutProvider()
    {
        // A row with no Provider/ProviderKey isn't OAuth-linked, so Unlink is a
        // no-op (returns false). Nothing is removed and no audit entry is written.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "plain@example.com",
            Provider = null,
            ProviderKey = null,
            IsVerified = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);

        var result = await _service.UnlinkAsync(userId, rowId, actorId);

        result.Should().BeFalse();
        await _userManager.DidNotReceive().RemoveLoginAsync(
            Arg.Any<User>(), Arg.Any<string>(), Arg.Any<string>());
        await _repository.DidNotReceive().RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetGoogleAsync_InvalidatesFullProfileCache()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId, UserId = userId, Email = "a@x.test",
            IsVerified = true, IsGoogle = false,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row });

        await _service.SetGoogleAsync(userId, rowId, userId);

        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
    }
}
