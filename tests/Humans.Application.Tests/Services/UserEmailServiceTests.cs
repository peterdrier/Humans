using AwesomeAssertions;
using System;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Profile;
using Humans.Domain.Entities;
using Humans.Testing;
using Microsoft.AspNetCore.Identity;
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
            _serviceProvider);
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
    public async Task DeleteEmailAsync_InvalidatesFullProfile()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _repository.GetByIdAndUserIdAsync(emailId, userId, Arg.Any<CancellationToken>())
            .Returns(new UserEmail
            {
                Id = emailId,
                UserId = userId,
                Email = "secondary@example.com",
                IsOAuth = false,
                IsVerified = true,
                IsNotificationTarget = false
            });

        await _service.DeleteEmailAsync(userId, emailId);

        await _repository.Received(1).RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _fullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteEmailAsync_OAuthEmail_ThrowsValidationException()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _repository.GetByIdAndUserIdAsync(emailId, userId, Arg.Any<CancellationToken>())
            .Returns(new UserEmail
            {
                Id = emailId,
                UserId = userId,
                Email = "signin@example.com",
                IsOAuth = true,
                IsVerified = true,
                IsNotificationTarget = true
            });

        var act = async () => await _service.DeleteEmailAsync(userId, emailId);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
        await _fullProfileInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
