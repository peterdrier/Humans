using Humans.Users.Services;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Attributes;
using Humans.Base.Controllers;
using System.Globalization;
using System.Web;
using Humans.Users.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Base.Constants;
using Humans.Users.Models;
using Microsoft.Extensions.Options;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Base;
using Humans.Base.Authorization;

using Humans.Google.Contracts;

namespace Humans.Users.Controllers;

[Authorize]
[Route("Profile")]
internal sealed class ProfileEmailsController(
    IUserManagement userService,
    UserManager<User> userManager,
    IUserEmailService userEmailService,
    ILogger<ProfileEmailsController> logger,
    IStringLocalizer<UsersResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer,
    ITicketService ticketQueryService,
    IAuthorizationService authorizationService,
    SignInManager<User> signInManager,
    IOptions<GoogleWorkspaceOptions> googleWorkspaceOptions) : HumansControllerBase(userService)
{
    private readonly EmailsViewModelBuilder emailsViewModelBuilder = new(
        userService, userManager, userEmailService, ticketQueryService, googleWorkspaceOptions.Value);
    private readonly IUserManagement _userService = userService;

    [HttpGet("Me/Emails")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Emails()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(user, ct: HttpContext.RequestAborted);
        return View(viewModel);
    }

    [HttpPost("Me/Emails/Add")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> AddEmail(EmailsViewModel model)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(model.NewEmail) || !ModelState.IsValid)
        {
            if (string.IsNullOrWhiteSpace(model.NewEmail))
                ModelState.AddModelError(nameof(model.NewEmail), localizer["Users_Profile_EnterEmail"].Value);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        // Verification can send mail; a browser disconnect must not interrupt the workflow.
        var result = await userEmailService.AddEmailAsync(user.Id, model.NewEmail, CancellationToken.None, user.Id);
        if (result.ErrorKey is { } errorKey)
        {
            ModelState.AddModelError(nameof(model.NewEmail), LocalizeEmailRefusal(errorKey));
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        logger.LogInformation(
            "Sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            model.NewEmail.Trim(), user.Id, result.IsConflict);
        SetAddedEmailFlash(model.NewEmail, result.IsConflict);

        return RedirectToAction(nameof(Emails));
    }

    private void SetAddedEmailFlash(string email, bool isConflict)
    {
        if (isConflict)
        {
            SetInfo(localizer["Users_Profile_EmailLinkedToAnotherAccount"].Value);
            return;
        }

        SetSuccess(string.Format(CultureInfo.CurrentCulture, localizer["Users_Profile_VerificationSent"].Value, email.Trim()));
    }

    [HttpGet("Me/Emails/Verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail(Guid userId, Guid emailId, string token)
    {
        if (string.IsNullOrEmpty(token) || emailId == Guid.Empty)
        {
            return VerifyEmailError(localizer["Users_Profile_InvalidVerificationLink"].Value);
        }

        var decodedToken = HttpUtility.UrlDecode(token);
        var result = await userEmailService.VerifyEmailAsync(userId, emailId, decodedToken);
        return result.ErrorKey is { } errorKey
            ? VerifyEmailError(localizer[errorKey].Value)
            : VerifyEmailSuccess(userId, result);
    }

    private IActionResult VerifyEmailSuccess(Guid userId, VerifyEmailResult result)
    {
        if (result.MergeRequestCreated)
        {
            logger.LogInformation(
                "User {UserId} verified email {Email} - merge request created",
                userId, result.Email);

            ViewData["Success"] = true;
            ViewData["Message"] = string.Format(
                CultureInfo.CurrentCulture,
                localizer["Users_Profile_EmailVerifiedMergeRequested"].Value,
                result.Email);
            return View("VerifyEmailResult");
        }

        logger.LogInformation(
            "User {UserId} verified email {Email}",
            userId, result.Email);

        ViewData["Success"] = true;
        ViewData["Message"] = string.Format(localizer["Users_Profile_EmailVerified"].Value, result.Email);
        return View("VerifyEmailResult");
    }
    private IActionResult VerifyEmailError(string message)
    {
        ViewData["Success"] = false;
        ViewData["Message"] = message;
        return View("VerifyEmailResult");
    }

    [HttpPost("Me/Emails/SetPrimary")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> SetPrimary(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        await SetPrimaryEmailAsync(user.Id, emailId, user.Id, isAdminContext: false, ct);
        return RedirectToAction(nameof(Emails));
    }

    [HttpPost("Me/Emails/SetVisibility")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> SetEmailVisibility(Guid emailId, string? visibility)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var parsedVisibility = ParseEmailVisibility(visibility);

        var result = await userEmailService.SetVisibilityAsync(user.Id, emailId, parsedVisibility, actorUserId: user.Id);
        if (!result.Succeeded)
        {
            logger.LogWarning("Email SetEmailVisibility refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                emailId, user.Id, user.Id, result.ErrorKey);
            SetError(LocalizeEmailRefusal(result.ErrorKey ?? "Common_Error"));
            return RedirectToAction(nameof(Emails));
        }
        SetSuccess(localizer["Users_Profile_EmailVisibilityUpdated"].Value);

        return RedirectToAction(nameof(Emails));
    }

    private static ContactFieldVisibility? ParseEmailVisibility(string? visibility) =>
        !string.IsNullOrEmpty(visibility) && Enum.TryParse<ContactFieldVisibility>(visibility, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : null;

    [HttpPost("Me/Emails/Delete")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> DeleteEmail(Guid emailId)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var deleted = await userEmailService.DeleteEmailAsync(user.Id, emailId, actorUserId: user.Id);
        if (deleted.Succeeded)
        {
            SetSuccess(localizer["Users_Profile_EmailDeleted"].Value);
        }
        else
        {
            logger.LogWarning("Email deletion refused for email {EmailId}, user {UserId}: {Reason}", emailId, user.Id, deleted.ErrorKey);
            SetError(LocalizeEmailRefusal(deleted.ErrorKey ?? "Users_EmailGrid_DeleteRejectedHasProvider"));
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost("Me/Emails/ClearPrimary")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> ClearPrimary(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var ok = await userEmailService.ClearPrimaryAsync(user.Id, emailId, user.Id, ct);
        SetPrimaryEmailClearedResult(ok, user.Id, emailId, user.Id);

        return RedirectToAction(nameof(Emails));
    }

    private void SetPrimaryEmailClearedResult(bool ok, Guid userId, Guid emailId, Guid actorId)
    {
        if (ok)
        {
            SetSuccess(localizer["Users_EmailGrid_PrimaryFlagCleared"].Value);
            return;
        }

        logger.LogWarning("Clear primary email refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
            emailId, userId, actorId, "Users_EmailGrid_ClearPrimaryRejected");
        SetError(localizer["Users_EmailGrid_ClearPrimaryRejected"].Value);
    }

    [HttpPost("Me/Emails/Link/{provider}")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Link(string provider, string? returnUrl = null)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        // Round-trip through ExternalLoginCallback so its link-while-signed-in branch runs.
        var resolvedReturnUrl = returnUrl ?? Url.Action(nameof(Emails)) ?? "/Profile/Me/Emails";
        var redirectUrl = Url.Action("ExternalLoginCallback", "Account", new { returnUrl = resolvedReturnUrl })
            ?? "/Account/ExternalLoginCallback";
        var props = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(props, provider);
    }

    [HttpPost("Me/Emails/Unlink/{id:guid}")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Unlink(Guid id, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var result = await userEmailService.UnlinkAsync(user.Id, id, user.Id, ct);
        SetEmailUnlinkedResult(result, user.Id, id, user.Id);

        return RedirectToAction(nameof(Emails));
    }

    private string LocalizeEmailRefusal(string key)
    {
        var message = localizer[key];
        return message.ResourceNotFound ? sharedLocalizer[key].Value : message.Value;
    }

    private void SetEmailUnlinkedResult(UnlinkEmailResult result, Guid userId, Guid? emailId, Guid actorId)
    {
        if (!result.Succeeded || result.ErrorKey is not null)
            logger.LogWarning("Email unlink refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                emailId, userId, actorId, result.ErrorKey ?? "Users_EmailGrid_UnlinkRejected");

        if (result.ErrorKey is { } errorKey)
        {
            SetError(LocalizeEmailRefusal(errorKey));
            return;
        }

        if (result.Succeeded)
        {
            SetSuccess(localizer["Users_EmailGrid_UnlinkSuccess"].Value);
            return;
        }

        SetError(localizer["Users_EmailGrid_UnlinkRejected"].Value);
    }

    // Keyed by (Provider, ProviderKey); the auth-method invariant is enforced server-side.
    [HttpPost("Me/LinkedAccounts/Unlink")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> UnlinkLinkedAccount(string provider, string providerKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerKey))
        {
            SetError(localizer["Users_EmailGrid_UnlinkRejected"].Value);
            return RedirectToAction(nameof(Emails));
        }

        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var result = await userEmailService.UnlinkAsync(user.Id, null, user.Id, ct, provider, providerKey);
        SetEmailUnlinkedResult(result, user.Id, null, user.Id);

        return RedirectToAction(nameof(Emails));
    }
    // No admin Link action: OAuth linking needs the target's own authentication.

    [HttpGet("{id:guid}/Admin/Emails")]
    public async Task<IActionResult> AdminEmails(Guid id, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.View);
        if (!authz.Succeeded)
            return Forbid();

        var targetUser = await userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        // A merge moves every email row to the survivor, and the forms post row ids under the route id. On a tombstone
        // every form would post a mismatched owner, so manage them on the survivor's page.
        var resolved = await _userService.GetUserInfoAsync(id, ct);
        if (resolved is not null && resolved.Id != id)
            return RedirectToAction(nameof(AdminEmails), new { id = resolved.Id });

        var viewModel = await BuildEmailsViewModelAsync(targetUser, isAdminContext: true, ct);
        return View("Emails", viewModel);
    }

    [HttpPost("{id:guid}/Admin/Emails/SetPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetPrimary(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        await SetPrimaryEmailAsync(id, emailId, actor.Id, isAdminContext: true, ct);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetPrimaryEmailAsync(
        Guid userId, Guid emailId, Guid actorId, bool isAdminContext, CancellationToken ct)
    {
        var result = await userEmailService.SetPrimaryAsync(userId, emailId, ct, actorId);
        if (!result.Succeeded)
        {
            logger.LogWarning("Email {Operation} refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                isAdminContext ? nameof(AdminSetPrimary) : nameof(SetPrimary), emailId, userId, actorId, result.ErrorKey);
            SetError(LocalizeEmailRefusal(result.ErrorKey ?? "Common_Error"));
            return;
        }
        SetSuccess(localizer["Users_Profile_NotificationTargetUpdated"].Value);
    }

    [HttpPost("{id:guid}/Admin/Emails/ClearPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminClearPrimary(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var ok = await userEmailService.ClearPrimaryAsync(id, emailId, actor.Id, ct);
        SetPrimaryEmailClearedResult(ok, id, emailId, actor.Id);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminAddEmail(Guid id, string email, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(localizer["Users_Profile_EnterEmail"].Value);
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var targetUser = await userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        // Verification can send mail; a browser disconnect must not interrupt the workflow.
        var result = await userEmailService.AddEmailAsync(id, email, CancellationToken.None, actor.Id);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Admin email addition refused for user {UserId} by {ActorId}: {ErrorKey}",
                id, actor.Id, errorKey);
            SetError(LocalizeEmailRefusal(errorKey));
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        SetSuccess(localizer["Users_EmailGrid_AdminAddSentVerification"].Value);
        return RedirectToAction(nameof(AdminEmails), new { id });
    }
    // Admin recovery: a verified row without a verification email.
    [HttpPost("{id:guid}/Admin/Emails/AddVerified")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminAddVerifiedEmail(Guid id, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(localizer["Users_Profile_EnterEmail"].Value);
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        var actor = await userManager.GetUserAsync(User);
        var targetUser = await userManager.FindByIdAsync(id.ToString());

        return await AdminAddVerifiedEmailAsync(id, email.Trim(), actor, targetUser, ct);
    }

    private async Task<IActionResult> AdminAddVerifiedEmailAsync(
        Guid userId,
        string email,
        User? actor,
        User? targetUser,
        CancellationToken ct)
    {
        if (actor is null)
            return Forbid();

        if (targetUser is null)
            return NotFound();

        var result = await userEmailService.AddVerifiedEmailAsync(userId, email, ct, actor.Id);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Admin verified email addition refused for user {UserId} by {ActorId}: {ErrorKey}",
                userId, actor.Id, errorKey);
            SetError(LocalizeEmailRefusal(errorKey));
        }
        else
        {
            ReportVerifiedEmailAdd(result.Added, email);
        }

        return RedirectToAction(nameof(AdminEmails), new { id = userId });
    }

    private void ReportVerifiedEmailAdd(bool inserted, string email)
    {
        if (!inserted)
        {
            SetInfo($"Email {email} already exists on this user — no change.");
            return;
        }

        SetSuccess($"Verified email {email} added.");
    }

    [HttpPost("{id:guid}/Admin/Emails/Verify")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminVerifyEmail(Guid id, Guid emailId, CancellationToken ct)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var result = await userEmailService.AdminMarkVerifiedAsync(id, emailId, actor.Id, ct);
        if (result.NoPendingVerification)
        {
            logger.LogWarning("Admin email verification refused: no pending plain email {EmailId} for user {UserId}", emailId, id);
            SetError("No email pending verification.");
        }
        else if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Admin email verification refused for email {EmailId}, user {UserId}: {ErrorKey}", emailId, id, errorKey);
            SetError(LocalizeEmailRefusal(errorKey));
        }
        else if (result.MergeRequestCreated)
        {
            SetSuccess(localizer["Users_EmailGrid_AdminVerifyMergeRequested"].Value);
        }
        else
        {
            SetSuccess(localizer["Users_EmailGrid_AdminVerifySuccess"].Value);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Unlink/{emailId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminUnlink(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var result = await userEmailService.UnlinkAsync(id, emailId, actor.Id, ct);
        SetEmailUnlinkedResult(result, id, emailId, actor.Id);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminDeleteEmail(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var deleted = await userEmailService.DeleteEmailAsync(id, emailId, ct, actor.Id);
        SetAdminEmailDeletedResult(id, emailId, actor.Id, deleted);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private void SetAdminEmailDeletedResult(Guid userId, Guid emailId, Guid actorId, DeleteEmailResult deleted)
    {
        if (!deleted.Succeeded)
        {
            logger.LogWarning("Admin email deletion refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                emailId, userId, actorId, deleted.ErrorKey);
            SetError(LocalizeEmailRefusal(deleted.ErrorKey ?? "Users_EmailGrid_DeleteRejectedHasProvider"));
            return;
        }

        SetSuccess(localizer["Users_Profile_EmailDeleted"].Value);
    }
    [HttpPost("{id:guid}/Admin/Emails/SetVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetVisibility(
        Guid id, Guid emailId, ContactFieldVisibility? visibility, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var result = await userEmailService.SetVisibilityAsync(id, emailId, visibility, ct, actor.Id);
        if (!result.Succeeded)
        {
            logger.LogWarning("Email AdminSetVisibility refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                emailId, id, actor.Id, result.ErrorKey);
            SetError(LocalizeEmailRefusal(result.ErrorKey ?? "Common_Error"));
            return RedirectToAction(nameof(AdminEmails), new { id });
        }
        SetSuccess(localizer["Users_Profile_EmailVisibilityUpdated"].Value);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private Task<EmailsViewModel> BuildEmailsViewModelAsync(
        User user, bool isAdminContext = false, CancellationToken ct = default) =>
        emailsViewModelBuilder.BuildAsync(user, isAdminContext, User.IsInRole(RoleNames.Admin), ct);

}
