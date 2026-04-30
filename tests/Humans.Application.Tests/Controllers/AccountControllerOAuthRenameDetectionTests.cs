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
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
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
    private readonly IStringLocalizer<Humans.Web.SharedResource> _localizer =
        Substitute.For<IStringLocalizer<Humans.Web.SharedResource>>();
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

        // Localizer returns the key as the value so tests can match without
        // pulling in the resx (consistent with other controller unit tests).
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new AccountController(
            _signInManager,
            _userManager,
            _clock,
            NullLogger<AccountController>.Instance,
            _userEmailService,
            _magicLinkService,
            _auditLogService,
            _localizer);
        _controller.Url = Substitute.For<IUrlHelper>();
        _controller.Url.IsLocalUrl(Arg.Any<string?>()).Returns(false);
        _controller.Url.Content(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        // TempData backing store for the controller — needed by tests that
        // assert TempData[ErrorMessage] is set on the link-failed branch.
        var tempDataProvider = Substitute.For<ITempDataProvider>();
        var tempDataDictionaryFactory = new TempDataDictionaryFactory(tempDataProvider);
        _controller.TempData = tempDataDictionaryFactory.GetTempData(
            new DefaultHttpContext());

        // Default to an unauthenticated HttpContext so the link-while-signed-in
        // branch in ExternalLoginCallback skips. Tests that exercise the
        // already-authenticated path override ControllerContext explicitly.
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
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
        var info = MakeInfo(newEmail);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);
        _magicLinkService.FindUserByVerifiedEmailAsync(newEmail, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        _userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(Arg.Any<User>(), Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);

        Guid createdUserId = Guid.Empty;
        _userEmailService.LinkAsync(
            Arg.Do<Guid>(id => createdUserId = id),
            Provider,
            ProviderKey,
            newEmail,
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).LinkAsync(
            Arg.Is<Guid>(id => id == createdUserId),
            Provider,
            ProviderKey,
            newEmail,
            Arg.Is<Guid>(id => id == createdUserId),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LinkByEmailBranch_TagsMatchingRowWithProviderAndProviderKey()
    {
        var existingUserId = Guid.NewGuid();
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

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).LinkAsync(
            existingUserId,
            Provider,
            ProviderKey,
            email,
            existingUserId,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExternalLoginCallback_AlreadyAuthenticated_AttachesGoogleIdentityToCurrentUser()
    {
        // User A is signed in via magic link with one verified UserEmail
        // (no Provider). They click "Link Google account" on /Profile/Me/Emails
        // and complete OAuth with a NEW email not yet on User A.
        //
        // Expected: the new email is added to User A as a verified row tagged
        // with Provider=Google, ProviderKey=sub-NEW. No second User is created.
        var currentUserId = Guid.NewGuid();
        var newEmail = "secondary@google.test";
        var info = MakeInfo(newEmail);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        // No existing OAuth login for this provider key — sign-in fails so the
        // callback proceeds past the success branch.
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);

        // Stand up an authenticated User principal on the controller.
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString()),
        }, authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        var currentUser = new User { Id = currentUserId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(currentUser);
        _userManager.AddLoginAsync(currentUser, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(currentUser).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        // No new user created via the create-new-account branch.
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());

        // OAuth identity attached to the currently-signed-in user via LinkAsync,
        // with actorUserId == userId (self-acting via authentication).
        await _userEmailService.Received(1).LinkAsync(
            currentUserId,
            Provider,
            ProviderKey,
            newEmail,
            currentUserId,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExternalLoginCallback_AlreadyAuthenticated_AddLoginFails_DoesNotFallthrough()
    {
        // User is signed in. AddLoginAsync fails (e.g. the OAuth login is already
        // attached to a different user, or transient EF error). The callback must
        // NOT fall through to the lockedout / email-match / create-new-user
        // branches that exist for the unauthenticated flow — that path can mint
        // a duplicate User row when the caller is already authenticated.
        var currentUserId = Guid.NewGuid();
        var newEmail = "secondary@google.test";
        var info = MakeInfo(newEmail);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString()),
        }, authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = services.BuildServiceProvider()
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
            {
                ActionName = nameof(AccountController.ExternalLoginCallback)
            }
        };

        // Re-attach TempData to the new ControllerContext.
        var tempDataProvider = Substitute.For<ITempDataProvider>();
        _controller.TempData = new TempDataDictionaryFactory(tempDataProvider)
            .GetTempData(httpContext);

        var currentUser = new User { Id = currentUserId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(currentUser);
        _userManager.AddLoginAsync(currentUser, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = "LoginAlreadyAssociated", Description = "x" }));

        // Url helper for redirect target.
        _controller.Url.IsLocalUrl(Arg.Any<string?>()).Returns(false);

        var result = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        // Did not fall through to create-new-user.
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
        // Did not fall through to email-match link branch on the unauthenticated path.
        await _magicLinkService.DidNotReceive().FindUserByVerifiedEmailAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Did not invoke LinkAsync since AddLoginAsync failed.
        await _userEmailService.DidNotReceive().LinkAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // Surfaced the failure as an error redirect to the emails page with the
        // localized error toast in TempData.
        result.Should().BeOfType<LocalRedirectResult>();
        var redirect = (LocalRedirectResult)result;
        redirect.Url.Should().Be("/Profile/Me/Emails");
        _controller.TempData.Should().ContainKey(Humans.Web.Constants.TempDataKeys.ErrorMessage);
        _controller.TempData[Humans.Web.Constants.TempDataKeys.ErrorMessage]
            .Should().Be("EmailGrid_LinkFailed");
    }
}
