using AwesomeAssertions;
using System;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Profiles;
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
    private readonly IUserInfoInvalidator _userInfoInvalidator = Substitute.For<IUserInfoInvalidator>();
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
            _userInfoInvalidator,
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

        await _userInfoInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
            // IsGoogle on the row being deleted so EnsureGoogleInvariantAsync
            // has work to do after the delete (re-stamping the surviving row).
            IsGoogle = false,
        };
        var keeping = new UserEmail
        {
            Id = keepingId,
            UserId = userId,
            Email = "primary@example.com",
            IsVerified = true,
            IsPrimary = true,
            // Already-stamped IsGoogle so the post-delete EnsureGoogleInvariantAsync
            // is a no-op and only DeleteEmailAsync's own invalidation runs.
            IsGoogle = true,
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
        await _userInfoInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
            Id = rowAId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsGoogle = true,
        };
        var rowB = new UserEmail
        {
            Id = rowBId,
            UserId = userId,
            Email = "b@x.test",
            IsVerified = true,
            IsGoogle = false,
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
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SetGoogleAsync_RejectsUnverified()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = false,
            IsGoogle = false,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);

        var result = await _service.SetGoogleAsync(userId, rowId, userId);

        result.Should().BeFalse();
        row.IsGoogle.Should().BeFalse();
        await _repository.DidNotReceive().SetGoogleExclusiveAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task ClearGoogleAsync_FlaggedRow_ClearsAndAudits()
    {
        // Issue 650: admin can clear IsGoogle from a row when the user is in
        // the duplicate-IsGoogle invariant-violation state. Two rows carry
        // IsGoogle, so the sole-row guard (issue 686) is satisfied.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsGoogle = true,
        };
        var sibling = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "b@x.test",
            IsVerified = true,
            IsGoogle = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row, sibling });

        var result = await _service.ClearGoogleAsync(userId, rowId, actorId);

        result.Should().BeTrue();
        row.IsGoogle.Should().BeFalse();
        await _repository.Received(1).UpdateAsync(row, Arg.Any<CancellationToken>());
        await _userInfoInvalidator.Received(1).InvalidateAsync(
            userId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailGoogleCleared,
            nameof(User), userId,
            Arg.Any<string>(),
            actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ClearGoogleAsync_NotFlagged_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsGoogle = false,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);

        var result = await _service.ClearGoogleAsync(userId, rowId, userId);

        result.Should().BeFalse();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ClearGoogleAsync_SoleIsGoogleRow_ReturnsFalse()
    {
        // Issue 686: clearing the only IsGoogle row would leave the user in
        // the ZeroIsGoogle state /Profile/Admin/EmailProblems flags as a bug.
        // Reject the call so admins can't reach that state via direct form
        // replay even after the view hides the button.
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsGoogle = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row });

        var result = await _service.ClearGoogleAsync(userId, rowId, userId);

        result.Should().BeFalse();
        row.IsGoogle.Should().BeTrue();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task ClearGoogleAsync_OnlyClearsTargetRow_LeavesOtherFlagAlone()
    {
        // Critical for the duplicate-IsGoogle remediation: clearing one row
        // must NOT touch the sibling row's flag, otherwise an admin trying
        // to fix "two flagged" would zero both out instead of resolving.
        var userId = Guid.NewGuid();
        var rowAId = Guid.NewGuid();
        var rowA = new UserEmail
        {
            Id = rowAId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsGoogle = true,
        };
        var rowB = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "b@x.test",
            IsVerified = true,
            IsGoogle = true,
        };
        _repository.GetByIdAndUserIdAsync(rowAId, userId, Arg.Any<CancellationToken>())
            .Returns(rowA);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { rowA, rowB });

        var result = await _service.ClearGoogleAsync(userId, rowAId, userId);

        result.Should().BeTrue();
        // Only the targeted row should have flowed through UpdateAsync —
        // SetGoogleExclusiveAsync (which would touch siblings) must not fire.
        await _repository.DidNotReceive().SetGoogleExclusiveAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(rowA, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ClearPrimaryAsync_FlaggedRow_ClearsAndAudits()
    {
        // Two rows carry IsPrimary (the duplicate-flag recovery scenario);
        // the sole-row guard (issue 686) is satisfied.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsPrimary = true,
        };
        var sibling = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "b@x.test",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row, sibling });

        var result = await _service.ClearPrimaryAsync(userId, rowId, actorId);

        result.Should().BeTrue();
        row.IsPrimary.Should().BeFalse();
        await _repository.Received(1).UpdateAsync(row, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailPrimaryCleared,
            nameof(User), userId,
            Arg.Any<string>(),
            actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ClearPrimaryAsync_SoleIsPrimaryRow_ReturnsFalse()
    {
        // Issue 686: clearing the only IsPrimary row would leave the user in
        // the ZeroIsPrimary state /Profile/Admin/EmailProblems flags as a bug.
        // Reject the call so admins can't reach that state via direct form
        // replay even after the view hides the button.
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row });

        var result = await _service.ClearPrimaryAsync(userId, rowId, userId);

        result.Should().BeFalse();
        row.IsPrimary.Should().BeTrue();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task ClearPrimaryAsync_OnlyUnverifiedSuccessor_ReturnsFalse()
    {
        // Issue 686 hardening: an unverified row carrying IsPrimary doesn't
        // count as a valid successor — clearing the sole verified IsPrimary
        // row would still leave the user in a zero-verified-primary state
        // EmailProblems flags as a bug. The guard mirrors the view's
        // `hasMultiplePrimary` (verified-only) and the scanner's
        // `verifiedPrimaryCount` semantics.
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsPrimary = true,
        };
        var unverifiedSibling = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "b@x.test",
            IsVerified = false,
            IsPrimary = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row, unverifiedSibling });

        var result = await _service.ClearPrimaryAsync(userId, rowId, userId);

        result.Should().BeFalse();
        row.IsPrimary.Should().BeTrue();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ClearPrimaryAsync_DoesNotAutoPromoteSuccessor()
    {
        // Admin-recovery semantics: after Clear, the user is left with the
        // remaining IsPrimary row(s) untouched. The service must NOT call
        // EnsurePrimaryInvariantAsync on this path — that helper would
        // demote the sibling and re-pick a winner. Use GetByUserIdForMutationAsync
        // (which the helper alone calls; the new sole-row guard reads via
        // GetByUserIdReadOnlyAsync) as the witness that the helper didn't run.
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var row = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsPrimary = true,
        };
        var sibling = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "b@x.test",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row, sibling });

        await _service.ClearPrimaryAsync(userId, rowId, userId);

        await _repository.DidNotReceive().GetByUserIdForMutationAsync(
            userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetEmailFlagViolationsAsync_FlagsMultipleGoogleAndBadPrimaryCounts()
    {
        // Two flagged scenarios + one healthy user. Healthy user must not
        // appear in the result; the violations list must include both
        // problem types with the right convenience flags set.
        var userMultipleGoogle = Guid.NewGuid();
        var userNoPrimary = Guid.NewGuid();
        var userMultiplePrimary = Guid.NewGuid();
        var userHealthy = Guid.NewGuid();

        var rows = new List<UserEmail>
        {
            // userMultipleGoogle: 2 verified rows, both IsGoogle=true, one IsPrimary=true.
            new() { Id = Guid.NewGuid(), UserId = userMultipleGoogle, Email = "a@x.test", IsVerified = true, IsGoogle = true,  IsPrimary = true  },
            new() { Id = Guid.NewGuid(), UserId = userMultipleGoogle, Email = "b@x.test", IsVerified = true, IsGoogle = true,  IsPrimary = false },

            // userNoPrimary: verified rows but none flagged primary.
            new() { Id = Guid.NewGuid(), UserId = userNoPrimary, Email = "c@x.test", IsVerified = true, IsGoogle = false, IsPrimary = false },
            new() { Id = Guid.NewGuid(), UserId = userNoPrimary, Email = "d@x.test", IsVerified = true, IsGoogle = false, IsPrimary = false },

            // userMultiplePrimary: two IsPrimary=true verified rows.
            new() { Id = Guid.NewGuid(), UserId = userMultiplePrimary, Email = "e@x.test", IsVerified = true, IsGoogle = false, IsPrimary = true },
            new() { Id = Guid.NewGuid(), UserId = userMultiplePrimary, Email = "f@x.test", IsVerified = true, IsGoogle = false, IsPrimary = true },

            // userHealthy: exactly one primary, no extra Google rows.
            new() { Id = Guid.NewGuid(), UserId = userHealthy, Email = "g@x.test", IsVerified = true, IsGoogle = true, IsPrimary = true },
            new() { Id = Guid.NewGuid(), UserId = userHealthy, Email = "h@x.test", IsVerified = true, IsGoogle = false, IsPrimary = false },
        };

        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(rows);
        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var violations = await _service.GetEmailFlagViolationsAsync();

        violations.Should().HaveCount(3);
        violations.Should().NotContain(v => v.UserId == userHealthy);

        var multipleGoogle = violations.Single(v => v.UserId == userMultipleGoogle);
        multipleGoogle.IsGoogleCount.Should().Be(2);
        multipleGoogle.HasMultipleGoogle.Should().BeTrue();

        var noPrimary = violations.Single(v => v.UserId == userNoPrimary);
        noPrimary.VerifiedPrimaryCount.Should().Be(0);
        noPrimary.HasPrimaryProblem.Should().BeTrue();
        noPrimary.HasMultipleGoogle.Should().BeFalse();

        var multiplePrimary = violations.Single(v => v.UserId == userMultiplePrimary);
        multiplePrimary.VerifiedPrimaryCount.Should().Be(2);
        multiplePrimary.HasPrimaryProblem.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetEmailFlagViolationsAsync_IgnoresUnverifiedRowsForPrimaryCheck()
    {
        // A user with zero verified rows is not in the violation set —
        // the "exactly one primary" rule applies only when verified rows
        // exist.
        var userId = Guid.NewGuid();
        var rows = new List<UserEmail>
        {
            new() { Id = Guid.NewGuid(), UserId = userId, Email = "a@x.test", IsVerified = false, IsGoogle = false, IsPrimary = false },
        };
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(rows);
        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var violations = await _service.GetEmailFlagViolationsAsync();

        violations.Should().BeEmpty();
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
        await _userInfoInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
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
            Id = rowId,
            UserId = userId,
            Email = "a@x.test",
            IsVerified = true,
            IsGoogle = false,
        };
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(row);
        _repository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { row });

        await _service.SetGoogleAsync(userId, rowId, userId);

        await _userInfoInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // -------------------------------------------------------------------------
    // IsPrimary invariant tests. EnsurePrimaryInvariantAsync is called from
    // AddVerifiedEmailAsync, AddEmailAsync, UnlinkAsync, and DeleteEmailAsync.
    // The OAuth-callback path used to flow through LinkAsync; it now flows
    // through ReconcileOAuthIdentityAsync (issue
    // nobodies-collective/Humans#697) — see UserEmailServiceReconcileOAuthTests.
    // -------------------------------------------------------------------------

    [HumansFact]
    public async Task AddVerifiedEmailAsync_FirstRow_SetsIsPrimaryTrueEvenForGmail()
    {
        // Magic-link signup: a non-Workspace email (gmail) goes through
        // AddVerifiedEmailAsync. Pre-fix this set IsPrimary=isNobodiesTeam=false,
        // leaving the user with NO primary row. The helper promotes the first
        // verified row regardless of domain.
        var userId = Guid.NewGuid();
        _repository.ExistsForUserAsync(
            userId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        UserEmail? added = null;
        await _repository.AddAsync(
            Arg.Do<UserEmail>(e => added = e),
            Arg.Any<CancellationToken>());

        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => added is null ? new List<UserEmail>() : new List<UserEmail> { added });

        await _service.AddVerifiedEmailAsync(userId, "alice@gmail.com");

        added.Should().NotBeNull();
        added!.IsPrimary.Should().BeTrue();
    }

    [HumansFact]
    public async Task AddVerifiedEmailAsync_NobodiesTeamRow_BecomesPrimaryOverExisting()
    {
        // Adding a @nobodies.team email when the user already has a primary
        // gmail row: the Workspace row wins per the priority rule in the helper.
        var userId = Guid.NewGuid();
        var existingPrimary = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@gmail.com",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.ExistsForUserAsync(
            userId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        UserEmail? added = null;
        await _repository.AddAsync(
            Arg.Do<UserEmail>(e => added = e),
            Arg.Any<CancellationToken>());

        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => added is null
                ? new List<UserEmail> { existingPrimary }
                : new List<UserEmail> { existingPrimary, added });

        await _service.AddVerifiedEmailAsync(userId, "alice@nobodies.team");

        added.Should().NotBeNull();
        added!.IsPrimary.Should().BeTrue();
        existingPrimary.IsPrimary.Should().BeFalse();
    }

    [HumansFact]
    public async Task UnlinkAsync_RemovesIsPrimary_AssignsSuccessor()
    {
        // User has the primary on the Google row + a secondary verified row.
        // Unlinking the Google row removes its primary status; the helper
        // promotes the remaining verified row.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var googleRowId = Guid.NewGuid();
        var googleRow = new UserEmail
        {
            Id = googleRowId,
            UserId = userId,
            Email = "google@example.com",
            Provider = "Google",
            ProviderKey = "sub-Z",
            IsVerified = true,
            IsPrimary = true,
        };
        var secondary = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "secondary@example.com",
            IsVerified = true,
            IsPrimary = false,
        };
        var user = new User { Id = userId };
        _repository.GetByIdAndUserIdAsync(googleRowId, userId, Arg.Any<CancellationToken>())
            .Returns(googleRow);
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.RemoveLoginAsync(user, "Google", "sub-Z")
            .Returns(IdentityResult.Success);

        // After RemoveAsync, the helper sees only the secondary (mock simulates
        // the post-removal state).
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { secondary });

        var result = await _service.UnlinkAsync(userId, googleRowId, actorId);

        result.Should().BeTrue();
        secondary.IsPrimary.Should().BeTrue();
    }

    [HumansFact]
    public async Task UnlinkAsync_RemovesNonPrimary_DoesNotChangePrimary()
    {
        // Primary is on row A; user unlinks row B (Google, non-primary). The
        // helper sees A still primary and the verified set is consistent — no
        // change.
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var googleRowId = Guid.NewGuid();
        var googleRow = new UserEmail
        {
            Id = googleRowId,
            UserId = userId,
            Email = "google@example.com",
            Provider = "Google",
            ProviderKey = "sub-Z",
            IsVerified = true,
            IsPrimary = false,
        };
        var primary = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "primary@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        var user = new User { Id = userId };
        _repository.GetByIdAndUserIdAsync(googleRowId, userId, Arg.Any<CancellationToken>())
            .Returns(googleRow);
        _userManager.FindByIdAsync(userId.ToString()).Returns(user);
        _userManager.RemoveLoginAsync(user, "Google", "sub-Z")
            .Returns(IdentityResult.Success);

        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { primary });

        var result = await _service.UnlinkAsync(userId, googleRowId, actorId);

        result.Should().BeTrue();
        primary.IsPrimary.Should().BeTrue();
    }

    [HumansFact]
    public async Task UnlinkAsync_RemoveLoginAsyncFails_DoesNotDeleteUserEmailRow_ReturnsFalse()
    {
        // Hard-fail: when RemoveLoginAsync fails, the UserEmail row MUST NOT be
        // removed. Otherwise AspNetUserLogins persists with a stale Google login
        // while the user thinks the unlink succeeded — they can still sign in.
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
            .Returns(IdentityResult.Failed(
                new IdentityError { Code = "SomeFailure", Description = "Identity refused" }));

        var result = await _service.UnlinkAsync(userId, rowId, actorId);

        result.Should().BeFalse();
        await _repository.DidNotReceive().RemoveAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        await _userInfoInvalidator.DidNotReceive().InvalidateAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // The OAuth-callback rename / backfill path used to be `UpdateEmailAsync`
    // on this service. It is now `ReconcileOAuthIdentityAsync` — see
    // `UserEmailServiceReconcileOAuthTests` (issue
    // nobodies-collective/Humans#697).

    // ─── VerifyEmailAsync row-Id disambiguation — issue #611 ──────────────
    // VerifyEmailAsync MUST load the pending row by the Id passed in (which
    // matches the token's purpose suffix), not by `FirstOrDefault(!IsVerified
    // && Provider == null)`. Otherwise a user with two pending plain rows
    // gets a confusing "expired or invalid" error when verifying via a link
    // that was actually issued for a different row.

    [HumansFact]
    public async Task VerifyEmailAsync_LoadsRowByEmailIdNotFirstPending()
    {
        var userId = Guid.NewGuid();
        var rowAId = Guid.NewGuid();
        var rowBId = Guid.NewGuid();
        // Row A and row B are BOTH unverified plain rows for the same user.
        // The verification link was issued for row B, so VerifyEmailAsync must
        // pick row B by Id — not row A via FirstOrDefault.
        var rowB = new UserEmail
        {
            Id = rowBId,
            UserId = userId,
            Email = "b@example.com",
            IsVerified = false,
            Provider = null,
            VerificationSentAt = _clock.GetCurrentInstant(),
        };

        var user = new User { Id = userId, DisplayName = "U" };
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _repository.GetByIdAndUserIdAsync(rowBId, userId, Arg.Any<CancellationToken>())
            .Returns(rowB);
        _repository.GetByIdAndUserIdAsync(rowAId, userId, Arg.Any<CancellationToken>())
            .Returns((UserEmail?)null);
        _repository.GetConflictingVerifiedEmailAsync(
                rowBId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((UserEmail?)null);
        _userManager.VerifyUserTokenAsync(
                user, TokenOptions.DefaultEmailProvider,
                $"UserEmailVerification:{rowBId}", "the-token-for-B")
            .Returns(true);

        var result = await _service.VerifyEmailAsync(userId, rowBId, "the-token-for-B");

        result.MergeRequestCreated.Should().BeFalse();
        result.Email.Should().Be("b@example.com");
        rowB.IsVerified.Should().BeTrue();
        // Row A was never loaded — VerifyEmailAsync must look up by the
        // emailId argument, not enumerate the user's other pending rows.
        await _repository.DidNotReceive().GetByIdAndUserIdAsync(
            rowAId, userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task VerifyEmailAsync_TokenForOtherRow_FailsWithoutVerifyingAnyRow()
    {
        // Token was issued for row A (purpose "...:rowAId"). The user clicks
        // the link for row B (caller passes rowBId). The token is invalid for
        // row B's purpose, so VerifyUserTokenAsync returns false and
        // ValidationException surfaces — but neither row gets verified.
        var userId = Guid.NewGuid();
        var rowAId = Guid.NewGuid();
        var rowBId = Guid.NewGuid();
        var rowB = new UserEmail
        {
            Id = rowBId,
            UserId = userId,
            Email = "b@example.com",
            IsVerified = false,
            Provider = null,
        };

        var user = new User { Id = userId, DisplayName = "U" };
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _repository.GetByIdAndUserIdAsync(rowBId, userId, Arg.Any<CancellationToken>())
            .Returns(rowB);
        // Token from row A's link is NOT valid against row B's purpose suffix.
        _userManager.VerifyUserTokenAsync(
                user, TokenOptions.DefaultEmailProvider,
                $"UserEmailVerification:{rowBId}", "token-issued-for-A")
            .Returns(false);

        var act = async () => await _service.VerifyEmailAsync(
            userId, rowBId, "token-issued-for-A");

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
        rowB.IsVerified.Should().BeFalse();
    }

    [HumansFact]
    public async Task VerifyEmailAsync_RowAlreadyVerified_ThrowsWithoutDoubleVerifying()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var verified = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "v@example.com",
            IsVerified = true,
            Provider = null,
        };

        var user = new User { Id = userId, DisplayName = "U" };
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(verified);

        var act = async () => await _service.VerifyEmailAsync(userId, rowId, "any-token");

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [HumansFact]
    public async Task VerifyEmailAsync_OAuthRow_Throws()
    {
        // Provider != null rows are tagged with an OAuth identity and verified
        // via the OAuth callback — never via a plain verification link.
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var oauth = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "oauth@example.com",
            IsVerified = false,
            Provider = "Google",
            ProviderKey = "sub-x",
        };

        var user = new User { Id = userId, DisplayName = "U" };
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(oauth);

        var act = async () => await _service.VerifyEmailAsync(userId, rowId, "any-token");

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    // ─── AdminMarkVerifiedAsync — issue #659 ─────────────────────────────
    // Admin manual verification: skip the token flow but reuse the same
    // duplicate-email merge-request branch as VerifyEmailAsync.

    [HumansFact]
    public async Task AdminMarkVerifiedAsync_PendingPlainRow_VerifiesAndAudits()
    {
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var pending = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "pending@example.com",
            IsVerified = false,
            Provider = null,
            VerificationSentAt = _clock.GetCurrentInstant(),
        };

        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(pending);
        _repository.GetConflictingVerifiedEmailAsync(
                rowId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((UserEmail?)null);

        var result = await _service.AdminMarkVerifiedAsync(userId, rowId, actorId);

        result.MergeRequestCreated.Should().BeFalse();
        result.Email.Should().Be("pending@example.com");
        pending.IsVerified.Should().BeTrue();
        await _repository.Received(1).UpdateAsync(pending, Arg.Any<CancellationToken>());
        await _userInfoInvalidator.Received(1).InvalidateAsync(
            userId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailManuallyVerified,
            nameof(User), userId,
            Arg.Any<string>(),
            actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AdminMarkVerifiedAsync_DuplicateEmail_CreatesMergeRequestWithoutVerifying()
    {
        // Mirrors VerifyEmailAsync's duplicate-handling: if the address is
        // already verified on another account, the admin path must NOT
        // silently complete verification — it creates a merge request so
        // the existing duplicate-account flow handles the collision.
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var pending = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "shared@example.com",
            IsVerified = false,
            Provider = null,
        };
        var conflicting = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = otherUserId,
            Email = "shared@example.com",
            IsVerified = true,
        };

        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(pending);
        _repository.GetConflictingVerifiedEmailAsync(
                rowId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(conflicting);
        _mergeService.HasPendingForEmailIdAsync(rowId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.AdminMarkVerifiedAsync(userId, rowId, actorId);

        result.MergeRequestCreated.Should().BeTrue();
        pending.IsVerified.Should().BeFalse();
        await _mergeService.Received(1).CreateAsync(
            Arg.Is<AccountMergeRequest>(m =>
                m.TargetUserId == userId
                && m.SourceUserId == otherUserId
                && m.PendingEmailId == rowId
                && m.Status == AccountMergeRequestStatus.Pending),
            Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().UpdateAsync(
            Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdminMarkVerifiedAsync_RowNotFound_Throws()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns((UserEmail?)null);

        var act = async () => await _service.AdminMarkVerifiedAsync(userId, rowId, Guid.NewGuid());

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [HumansFact]
    public async Task AdminMarkVerifiedAsync_AlreadyVerified_Throws()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(new UserEmail
            {
                Id = rowId,
                UserId = userId,
                Email = "v@x.test",
                IsVerified = true,
                Provider = null,
            });

        var act = async () => await _service.AdminMarkVerifiedAsync(userId, rowId, Guid.NewGuid());

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [HumansFact]
    public async Task AdminMarkVerifiedAsync_OAuthRow_Throws()
    {
        // Provider != null rows are verified through the OAuth callback,
        // not via this admin path — even an admin shouldn't bypass that.
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        _repository.GetByIdAndUserIdAsync(rowId, userId, Arg.Any<CancellationToken>())
            .Returns(new UserEmail
            {
                Id = rowId,
                UserId = userId,
                Email = "oauth@x.test",
                IsVerified = false,
                Provider = "Google",
                ProviderKey = "sub-x",
            });

        var act = async () => await _service.AdminMarkVerifiedAsync(userId, rowId, Guid.NewGuid());

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Issue nobodies-collective/Humans#687: UserEmail.IsGoogle is sole source
    // of truth. Tests for the orchestrator + EnsureGoogleInvariantAsync.
    // ─────────────────────────────────────────────────────────────────────────

    // Coverage of the OAuth-signup IsGoogle / IsPrimary stamp moved to
    // `UserEmailServiceReconcileOAuthTests` along with the rest of the
    // OAuth-callback flow (issue nobodies-collective/Humans#697).

    [HumansFact]
    public async Task AddVerifiedEmailAsync_NobodiesTeamRow_BecomesIsGooglePromotedOverExistingPersonalRow()
    {
        // When a personal Google row exists (IsGoogle=true) and a @nobodies.team
        // row is added, EnsureGoogleInvariantAsync's @nobodies.team-wins
        // precedence flips IsGoogle to the new Workspace row.
        var userId = Guid.NewGuid();
        var personalRow = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@gmail.com",
            IsVerified = true,
            IsPrimary = true,
            IsGoogle = true,
            UpdatedAt = Instant.FromUtc(2026, 4, 10, 0, 0),
        };

        _repository.ExistsForUserAsync(userId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        UserEmail? added = null;
        _repository.AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                added = call.Arg<UserEmail>();
                return Task.CompletedTask;
            });

        // After AddAsync, repository returns both rows.
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => new List<UserEmail> { personalRow, added! });

        await _service.AddVerifiedEmailAsync(userId, "alice@nobodies.team");

        added.Should().NotBeNull();
        added!.IsGoogle.Should().BeTrue("@nobodies.team row wins Google precedence");
        personalRow.IsGoogle.Should().BeFalse("EnsureGoogleInvariantAsync demoted the personal row");
    }

    [HumansFact]
    public async Task AddProvisionedEmailAsync_NewUser_StampsIsGoogleOnFirstRow()
    {
        // Spec acceptance criterion: AccountProvisioningService path enforces
        // the invariant — the first UserEmail row added for a brand-new user
        // becomes IsGoogle (it's the only verified row).
        var userId = Guid.NewGuid();
        UserEmail? added = null;

        _repository.ExistsForUserAsync(userId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _repository.AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                added = call.Arg<UserEmail>();
                return Task.CompletedTask;
            });
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => added is null ? new List<UserEmail>() : new List<UserEmail> { added });

        await _service.AddProvisionedEmailAsync(userId, "alice@example.com");

        added.Should().NotBeNull();
        added!.IsPrimary.Should().BeTrue("the only verified row wins Primary");
        added.IsGoogle.Should().BeTrue("the only verified row wins Google");
    }

    [HumansFact]
    public async Task AddProvisionedEmailAsync_RowAlreadyExistsForUser_IsIdempotent()
    {
        var userId = Guid.NewGuid();
        _repository.ExistsForUserAsync(userId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.AddProvisionedEmailAsync(userId, "alice@example.com");

        await _repository.DidNotReceive()
            .AddAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
    }
}
