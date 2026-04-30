using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Unit tests for the OAuth-callback rename detection and Provider/ProviderKey
/// tagging branches in <see cref="AccountController.ExternalLoginCallback"/>.
/// </summary>
public class AccountControllerOAuthRenameDetectionTests
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IMagicLinkService _magicLinkService = Substitute.For<IMagicLinkService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 30, 12, 0));
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly AccountController _controller;

    private const string Provider = "Google";
    private const string ProviderKey = "google-sub-12345";

    public AccountControllerOAuthRenameDetectionTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);

        var contextAccessor = Substitute.For<IHttpContextAccessor>();
        var claimsFactory = Substitute.For<IUserClaimsPrincipalFactory<User>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        identityOptions.Value.Returns(new IdentityOptions());
        var schemeProvider = Substitute.For<IAuthenticationSchemeProvider>();
        var userConfirmation = Substitute.For<IUserConfirmation<User>>();

        _signInManager = Substitute.For<SignInManager<User>>(
            _userManager, contextAccessor, claimsFactory, identityOptions,
            NullLogger<SignInManager<User>>.Instance, schemeProvider, userConfirmation);

        _controller = new AccountController(
            _signInManager,
            _userManager,
            _clock,
            NullLogger<AccountController>.Instance,
            _userEmailService,
            _magicLinkService,
            _auditLogService);
        _controller.Url = Substitute.For<IUrlHelper>();
        _controller.Url.IsLocalUrl(Arg.Any<string?>()).Returns(false);
        _controller.Url.Content(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
    }

    private static ExternalLoginInfo MakeInfo(string email, string name = "Test User")
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.NameIdentifier, ProviderKey),
        }, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new ExternalLoginInfo(principal, Provider, ProviderKey, "Google");
    }

    [HumansFact]
    public async Task SuccessBranch_RenameDetected_RewritesUserEmailAndAudits()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var oldEmail = "old@nobodies.team";
        var newEmail = "new@nobodies.team";
        var info = MakeInfo(newEmail);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        var existingRow = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = oldEmail,
            IsVerified = true,
            Provider = Provider,
            ProviderKey = ProviderKey,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _userEmailService.FindByProviderKeyAsync(Provider, ProviderKey, Arg.Any<CancellationToken>())
            .Returns(new UserEmailProviderMatch(existingRow.Id, existingRow.UserId, existingRow.Email));

        var result = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        result.Should().NotBeNull();
        await _userEmailService.Received(1).RewriteEmailAddressAsync(
            userId, oldEmail, newEmail, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.GoogleEmailRenamed,
            nameof(User), userId,
            Arg.Is<string>(s => s.Contains(oldEmail) && s.Contains(newEmail) && s.Contains(ProviderKey)),
            nameof(AccountController),
            relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
    }

    [HumansFact]
    public async Task SuccessBranch_EmailsMatch_NoRewriteNoAudit()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var email = "stable@nobodies.team";
        var info = MakeInfo(email);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        var existingRow = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = email,
            IsVerified = true,
            Provider = Provider,
            ProviderKey = ProviderKey,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _userEmailService.FindByProviderKeyAsync(Provider, ProviderKey, Arg.Any<CancellationToken>())
            .Returns(new UserEmailProviderMatch(existingRow.Id, existingRow.UserId, existingRow.Email));

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.DidNotReceive().RewriteEmailAddressAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SuccessBranch_CaseOnlyDifference_NoRewrite()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var info = MakeInfo("Stable@Nobodies.Team");

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        var existingRow = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "stable@nobodies.team",
            IsVerified = true,
            Provider = Provider,
            ProviderKey = ProviderKey,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _userEmailService.FindByProviderKeyAsync(Provider, ProviderKey, Arg.Any<CancellationToken>())
            .Returns(new UserEmailProviderMatch(existingRow.Id, existingRow.UserId, existingRow.Email));

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.DidNotReceive().RewriteEmailAddressAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NewUserBranch_TagsCreatedRowWithProviderAndProviderKey()
    {
        var newEmail = "fresh@example.com";
        var newRowId = Guid.NewGuid();
        var info = MakeInfo(newEmail);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);
        _magicLinkService.FindUserByVerifiedEmailAsync(newEmail, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        _userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(Arg.Any<User>(), Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);

        Guid createdUserId = Guid.Empty;
        await _userEmailService.AddOAuthEmailAsync(
            Arg.Do<Guid>(id => createdUserId = id),
            newEmail,
            Arg.Any<CancellationToken>());

        _userEmailService.GetUserEmailsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new List<UserEmailEditDto>
            {
                new(
                    Id: newRowId,
                    Email: newEmail,
                    IsVerified: true,
                    IsGoogle: false,
                    Provider: null,
                    ProviderKey: null,
                    IsNotificationTarget: true,
                    Visibility: ContactFieldVisibility.BoardOnly,
                    IsPendingVerification: false,
                    IsMergePending: false)
            });

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).SetProviderAsync(
            Arg.Is<Guid>(id => id == createdUserId),
            newRowId, Provider, ProviderKey, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LinkByEmailBranch_TagsMatchingRowWithProviderAndProviderKey()
    {
        var existingUserId = Guid.NewGuid();
        var existingRowId = Guid.NewGuid();
        var email = "linked@example.com";
        var info = MakeInfo(email);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);

        var existingUser = new User { Id = existingUserId };
        _magicLinkService.FindUserByVerifiedEmailAsync(email, Arg.Any<CancellationToken>())
            .Returns(existingUser);

        _userManager.AddLoginAsync(existingUser, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(existingUser).Returns(IdentityResult.Success);

        _userEmailService.GetUserEmailsAsync(existingUserId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmailEditDto>
            {
                new(
                    Id: existingRowId,
                    Email: email,
                    IsVerified: true,
                    IsGoogle: false,
                    Provider: null,
                    ProviderKey: null,
                    IsNotificationTarget: true,
                    Visibility: ContactFieldVisibility.BoardOnly,
                    IsPendingVerification: false,
                    IsMergePending: false)
            });

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).SetProviderAsync(
            existingUserId, existingRowId, Provider, ProviderKey, Arg.Any<CancellationToken>());
    }
}
