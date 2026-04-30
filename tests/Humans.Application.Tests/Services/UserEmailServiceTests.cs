using AwesomeAssertions;
using System;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Profile;
using Humans.Domain.Entities;
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
            _serviceProvider,
            NullLogger<UserEmailService>.Instance);
    }

    [HumansFact]
    public async Task SetNotificationTargetAsync_VerifiedTarget_InvalidatesFullProfile()
    {
        var userId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var emails = new List<UserEmail>
        {
            new()
            {
                Id = targetId, UserId = userId, Email = "target@example.com",
                IsVerified = true, IsNotificationTarget = false
            },
            new()
            {
                Id = otherId, UserId = userId, Email = "other@example.com",
                IsVerified = true, IsNotificationTarget = true
            }
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(emails);

        await _service.SetNotificationTargetAsync(userId, targetId);

        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
        emails.Single(e => e.Id == targetId).IsNotificationTarget.Should().BeTrue();
        emails.Single(e => e.Id == otherId).IsNotificationTarget.Should().BeFalse();
    }

    [HumansFact]
    public async Task SetNotificationTargetAsync_UnverifiedTarget_DoesNotInvalidate()
    {
        var userId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var emails = new List<UserEmail>
        {
            new()
            {
                Id = targetId, UserId = userId, Email = "unverified@example.com",
                IsVerified = false, IsNotificationTarget = false
            }
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(emails);

        var act = async () => await _service.SetNotificationTargetAsync(userId, targetId);

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
            IsNotificationTarget = false,
        };
        var keeping = new UserEmail
        {
            Id = keepingId,
            UserId = userId,
            Email = "primary@example.com",
            IsVerified = true,
            IsNotificationTarget = true,
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
    public async Task DeleteEmailAsync_OAuthFlaggedRowWithOtherAuthMethods_DeletesSuccessfully()
    {
        // Replaces the old "IsOAuth blocks delete" rule. Under PR 1's
        // preserve-auth-method invariant, the OAuth-flagged UserEmail row IS
        // deletable as long as another auth method remains (here: a second
        // verified UserEmail). The AspNetUserLogins row is independent of the
        // UserEmail row so OAuth sign-in still works after the delete.
        var userId = Guid.NewGuid();
        var oauthRowId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();
        var oauthRow = new UserEmail
        {
            Id = oauthRowId,
            UserId = userId,
            Email = "google@example.com",
            Provider = "Google",
            ProviderKey = "test-oauth",
            IsVerified = true,
            IsNotificationTarget = true,
        };
        var secondary = new UserEmail
        {
            Id = secondaryId,
            UserId = userId,
            Email = "personal@example.com",
            IsVerified = true,
            IsNotificationTarget = false,
        };
        _repository.GetByIdAndUserIdAsync(oauthRowId, userId, Arg.Any<CancellationToken>())
            .Returns(oauthRow);
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { oauthRow, secondary });
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId });
        _userManager.GetLoginsAsync(Arg.Any<User>())
            .Returns(new List<UserLoginInfo>());

        await _service.DeleteEmailAsync(userId, oauthRowId);

        await _repository.Received(1).RemoveAsync(oauthRow, Arg.Any<CancellationToken>());
        // Notification-target hand-off — successor should now be flagged.
        secondary.IsNotificationTarget.Should().BeTrue();
        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
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
            IsNotificationTarget = true,
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
            IsNotificationTarget = true,
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
                IsNotificationTarget = false,
            });

        await _service.DeleteEmailAsync(userId, emailId);

        await _repository.Received(1).RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        // GetLoginsAsync should not even be called since the verified branch is skipped.
        await _userManager.DidNotReceive().GetLoginsAsync(Arg.Any<User>());
    }
}
