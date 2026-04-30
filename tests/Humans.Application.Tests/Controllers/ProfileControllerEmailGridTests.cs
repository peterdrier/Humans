using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization.UserEmail;
using Humans.Application.Configuration;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Unit tests for the self-route controller actions on
/// <see cref="ProfileController"/> that wrap the new UserEmailService grid
/// methods (SetGoogle, Link, Unlink) and the SetPrimary rename. Covers PR 4
/// tasks 13/14/15 of the email/OAuth decoupling plan.
/// </summary>
public class ProfileControllerEmailGridTests
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly ProfileController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public ProfileControllerEmailGridTests()
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

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new ProfileController(
            _userManager,
            Substitute.For<IProfileService>(),
            Substitute.For<IContactFieldService>(),
            _emailService,
            _userEmailService,
            Substitute.For<ICommunicationPreferenceService>(),
            _auditLogService,
            Substitute.For<IOnboardingService>(),
            Substitute.For<IRoleAssignmentService>(),
            Substitute.For<IShiftSignupService>(),
            Substitute.For<IShiftManagementService>(),
            Substitute.For<IGdprExportService>(),
            Substitute.For<IConfiguration>(),
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            Substitute.For<ITicketQueryService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IEmailOutboxService>(),
            _cache,
            new FakeClock(Instant.FromUtc(2026, 4, 30, 12, 0)),
            _authorizationService,
            Substitute.For<IUserService>(),
            Substitute.For<IHttpClientFactory>(),
            _signInManager,
            Options.Create(new GoogleWorkspaceOptions()));

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
        }, authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        _controller.Url = Substitute.For<IUrlHelper>();
        _controller.Url.Action(Arg.Any<UrlActionContext>()).Returns("/Profile/Me/Emails");

        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new User { Id = _userId });

        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
    }

    [HumansFact]
    public async Task SetGoogle_AsSelf_CallsSetGoogleAsync_AndRedirectsToGrid()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.SetGoogleAsync(_userId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.SetGoogle(emailId, CancellationToken.None);

        await _userEmailService.Received(1).SetGoogleAsync(
            _userId, emailId, _userId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansFact]
    public async Task SetGoogle_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.SetGoogle(emailId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().SetGoogleAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Link_AsSelf_ReturnsChallengeResult_WithProvider()
    {
        // Url.Action returns the callback URL (with returnUrl appended) when
        // invoked with the ExternalLoginCallback target; the constructor's
        // generic Url.Action stub already returns "/Profile/Me/Emails", but
        // we override the callback-specific call so the props.RedirectUri
        // assertion below is meaningful.
        _controller.Url.Action(Arg.Is<UrlActionContext>(ctx =>
                ctx.Action == "ExternalLoginCallback" && ctx.Controller == "Account"))
            .Returns("/Account/ExternalLoginCallback?returnUrl=%2FProfile%2FMe%2FEmails");

        AuthenticationProperties? capturedProps = null;
        _signInManager.ConfigureExternalAuthenticationProperties("Google", Arg.Any<string>())
            .Returns(ci =>
            {
                capturedProps = new AuthenticationProperties { RedirectUri = ci.ArgAt<string>(1) };
                return capturedProps;
            });

        var result = await _controller.Link("Google", returnUrl: "/Profile/Me/Emails");

        result.Should().BeOfType<ChallengeResult>()
            .Which.AuthenticationSchemes.Should().Contain("Google");
        // The Link flow MUST route through AccountController.ExternalLoginCallback
        // so the link-while-signed-in branch (UserManager.AddLoginAsync) fires.
        // Going straight to /Profile/Me/Emails would skip that branch.
        capturedProps.Should().NotBeNull();
        capturedProps!.RedirectUri.Should().Contain("ExternalLoginCallback");
    }

    [HumansFact]
    public async Task Link_AuthorizationFails_ReturnsForbid()
    {
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.Link("Google", returnUrl: null);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Unlink_AsSelf_CallsUnlinkAsync_AndRedirectsToGrid()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.UnlinkAsync(_userId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.Unlink(emailId, CancellationToken.None);

        await _userEmailService.Received(1).UnlinkAsync(
            _userId, emailId, _userId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansFact]
    public async Task Unlink_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.Unlink(emailId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().UnlinkAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Task 16 — admin grid actions parameterized by {userId}.
    // Actor user is _userId (current user). Target user is a separate guid passed
    // via the route. AuthorizeAsync is set up to succeed in the constructor; the
    // Edit + Edit-equivalent UserEmailOperations.Edit handler will succeed for
    // self-or-admin via the resource-based authorization path. These tests assert
    // the controller passes the route userId (target) to the service and the
    // current user's id as actorUserId.
    // -------------------------------------------------------------------------

    [HumansFact]
    public async Task AdminSetGoogle_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _userEmailService.SetGoogleAsync(targetUserId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.AdminSetGoogle(targetUserId, emailId, CancellationToken.None);

        await _userEmailService.Received(1).SetGoogleAsync(
            targetUserId, emailId, _userId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminSetPrimary_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();

        var result = await _controller.AdminSetPrimary(targetUserId, emailId, CancellationToken.None);

        await _userEmailService.Received(1).SetPrimaryAsync(
            targetUserId, emailId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminAddEmail_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        const string newEmail = "added@example.com";
        _userManager.FindByIdAsync(targetUserId.ToString())
            .Returns(new User { Id = targetUserId, DisplayName = "Target User", PreferredLanguage = "en" });
        _userEmailService.AddEmailAsync(targetUserId, newEmail, Arg.Any<CancellationToken>())
            .Returns(new Humans.Application.DTOs.AddEmailResult("token", IsConflict: false));

        var result = await _controller.AdminAddEmail(targetUserId, newEmail, CancellationToken.None);

        await _userEmailService.Received(1).AddEmailAsync(
            targetUserId, newEmail, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminAddEmail_AuditsWithAdminAsActor()
    {
        var targetUserId = Guid.NewGuid();
        const string newEmail = "added@example.com";
        _userManager.FindByIdAsync(targetUserId.ToString())
            .Returns(new User { Id = targetUserId, DisplayName = "Target User", PreferredLanguage = "en" });
        _userEmailService.AddEmailAsync(targetUserId, newEmail, Arg.Any<CancellationToken>())
            .Returns(new Humans.Application.DTOs.AddEmailResult("token", IsConflict: false));

        var result = await _controller.AdminAddEmail(targetUserId, newEmail, CancellationToken.None);

        // Admin-path audit symmetric with AdminSetPrimary / AdminDeleteEmail —
        // actor is the signed-in admin (_userId), entity is the target user.
        // AddEmailAsync doesn't return the new row's Id, so no relatedEntityId.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailAdded,
            nameof(User), targetUserId,
            Arg.Any<string>(),
            _userId,
            relatedEntityId: null,
            relatedEntityType: null);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminAddEmail_SendsVerificationEmail_ToTargetUser_WithReturnedToken()
    {
        var targetUserId = Guid.NewGuid();
        const string newEmail = "added@example.com";
        const string token = "verification-token-abc";

        // Stub Url.Action to embed the encoded token in the returned URL so the
        // test can verify the verification URL is built from the AddEmailAsync token.
        _controller.Url.Action(Arg.Is<UrlActionContext>(ctx => ctx.Action == "VerifyEmail"))
            .Returns(ci =>
            {
                var ctx = ci.Arg<UrlActionContext>();
                var routeValues = new Microsoft.AspNetCore.Routing.RouteValueDictionary(ctx.Values);
                return $"/Profile/Me/Emails/Verify?userId={routeValues["userId"]}&token={routeValues["token"]}";
            });

        _userManager.FindByIdAsync(targetUserId.ToString())
            .Returns(new User { Id = targetUserId, DisplayName = "Target User", PreferredLanguage = "es" });
        _userEmailService.AddEmailAsync(targetUserId, newEmail, Arg.Any<CancellationToken>())
            .Returns(new Humans.Application.DTOs.AddEmailResult(token, IsConflict: false));

        var result = await _controller.AdminAddEmail(targetUserId, newEmail, CancellationToken.None);

        // Verification email goes to the target email being added, with the token
        // returned by AddEmailAsync — NOT discarded. Recipient name and culture
        // come from the target user, not the admin.
        await _emailService.Received(1).SendEmailVerificationAsync(
            newEmail,
            "Target User",
            Arg.Is<string>(url => url.Contains(token, StringComparison.Ordinal)),
            false,
            "es",
            Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminUnlink_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _userEmailService.UnlinkAsync(targetUserId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.AdminUnlink(targetUserId, emailId, CancellationToken.None);

        await _userEmailService.Received(1).UnlinkAsync(
            targetUserId, emailId, _userId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminDeleteEmail_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _userEmailService.DeleteEmailAsync(targetUserId, emailId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.AdminDeleteEmail(targetUserId, emailId, CancellationToken.None);

        await _userEmailService.Received(1).DeleteEmailAsync(
            targetUserId, emailId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminSetVisibility_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();

        var result = await _controller.AdminSetVisibility(
            targetUserId, emailId, ContactFieldVisibility.BoardOnly, CancellationToken.None);

        await _userEmailService.Received(1).SetVisibilityAsync(
            targetUserId, emailId, ContactFieldVisibility.BoardOnly, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminSetGoogle_AuthorizationFails_ReturnsForbid()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.AdminSetGoogle(targetUserId, emailId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().SetGoogleAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Findings O, P, Q — self-path auth gates and audit-log symmetry.
    // O: DeleteEmail self-action must call AuthorizeAsync (UserEmailOperations.Edit).
    // P: SetEmailVisibility self-action must call AuthorizeAsync.
    // Q: SetPrimary, DeleteEmail, SetEmailVisibility self-actions must audit-log,
    //    matching the admin path that already audits.
    // -------------------------------------------------------------------------

    [HumansFact]
    public async Task DeleteEmail_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.DeleteEmail(emailId);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().DeleteEmailAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task SetEmailVisibility_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.SetEmailVisibility(emailId, "BoardOnly");

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().SetVisibilityAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<ContactFieldVisibility?>());
    }

    [HumansFact]
    public async Task SetPrimary_AsSelf_AuditsWithUserAsActor()
    {
        var emailId = Guid.NewGuid();

        var result = await _controller.SetPrimary(emailId, CancellationToken.None);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailPrimarySet,
            nameof(User), _userId,
            Arg.Any<string>(),
            _userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansFact]
    public async Task DeleteEmail_AsSelf_AuditsWithUserAsActor()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.DeleteEmailAsync(_userId, emailId).Returns(true);

        var result = await _controller.DeleteEmail(emailId);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailDeleted,
            nameof(User), _userId,
            Arg.Any<string>(),
            _userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansFact]
    public async Task SetEmailVisibility_AsSelf_AuditsWithUserAsActor()
    {
        var emailId = Guid.NewGuid();

        var result = await _controller.SetEmailVisibility(emailId, "BoardOnly");

        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailVisibilityChanged,
            nameof(User), _userId,
            Arg.Any<string>(),
            _userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }
}
