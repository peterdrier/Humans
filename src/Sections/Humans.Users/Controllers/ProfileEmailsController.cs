using Humans.Users.Services;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Attributes;
using Humans.Base.Controllers;
using System.Globalization;
using System.Web;
using Humans.Users.Authorization;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Base.Constants;
using Humans.Users.Models;
using Microsoft.Extensions.Options;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Base;
using Humans.Base.Authorization;

using Humans.GoogleIntegration.Contracts;

namespace Humans.Users.Controllers;

[Authorize]
[Route("Profile")]
internal sealed class ProfileEmailsController(
    IUserManagement userService,
    UserManager<User> userManager,
    IEmailService emailService,
    UsersEmails emailMessages,
    IUserEmailService userEmailService,
    IAuditLogService auditLogService,
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

        var result = await userEmailService.AddEmailAsync(user.Id, model.NewEmail);
        if (result.ErrorKey is { } errorKey)
        {
            ModelState.AddModelError(nameof(model.NewEmail), LocalizeEmailRefusal(errorKey));
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        await SendAddedEmailVerificationAsync(user, model.NewEmail, result);
        logger.LogInformation(
            "Sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            model.NewEmail.Trim(), user.Id, result.IsConflict);
        SetAddedEmailFlash(model.NewEmail, result.IsConflict);

        return RedirectToAction(nameof(Emails));
    }

    private async Task SendAddedEmailVerificationAsync(User user, string email, AddEmailResult result)
    {
        var trimmedEmail = email.Trim();
        var verificationUrl = Url.Action(
            nameof(VerifyEmail),
            "ProfileEmails",
            new { userId = user.Id, emailId = result.EmailId, token = HttpUtility.UrlEncode(result.Token) },
            Request.Scheme);

        var info = await _userService.GetUserInfoAsync(user.Id);

        var message = emailMessages.EmailVerification(
            user.Id,
            trimmedEmail,
            info?.BurnerName ?? string.Empty,
            verificationUrl!,
            result.IsConflict,
            user.PreferredLanguage.IsSupportedCultureCode()
                ? user.PreferredLanguage : CultureCatalog.DefaultCultureCode);
        await emailService.SendAsync(message);
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

        var result = await userEmailService.SetVisibilityAsync(user.Id, emailId, parsedVisibility);
        if (!result.Succeeded)
        {
            logger.LogWarning("Email SetEmailVisibility refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                emailId, user.Id, user.Id, result.ErrorKey);
            SetError(LocalizeEmailRefusal(result.ErrorKey ?? "Common_Error"));
            return RedirectToAction(nameof(Emails));
        }
        await LogSelfEmailVisibilityChangedAsync(user.Id, emailId, parsedVisibility);
        SetSuccess(localizer["Users_Profile_EmailVisibilityUpdated"].Value);

        return RedirectToAction(nameof(Emails));
    }

    private static ContactFieldVisibility? ParseEmailVisibility(string? visibility) =>
        !string.IsNullOrEmpty(visibility) && Enum.TryParse<ContactFieldVisibility>(visibility, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : null;

    private Task LogSelfEmailVisibilityChangedAsync(
        Guid userId,
        Guid emailId,
        ContactFieldVisibility? visibility) =>
        auditLogService.LogAsync(
            UsersAuditActions.UserEmailVisibilityChanged,
            nameof(User), userId,
            $"Changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}",
            userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
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

        var deleted = await userEmailService.DeleteEmailAsync(user.Id, emailId);
        if (deleted.Succeeded)
        {
            await LogSelfEmailDeletedAsync(user.Id, emailId);
            SetSuccess(localizer["Users_Profile_EmailDeleted"].Value);
        }
        else
        {
            logger.LogWarning("Email deletion refused for email {EmailId}, user {UserId}: {Reason}", emailId, user.Id, deleted.ErrorKey);
            SetError(LocalizeEmailRefusal(deleted.ErrorKey ?? "Users_EmailGrid_DeleteRejectedHasProvider"));
        }

        return RedirectToAction(nameof(Emails));
    }

    private Task LogSelfEmailDeletedAsync(Guid userId, Guid emailId) =>
        auditLogService.LogAsync(
            UsersAuditActions.UserEmailDeleted,
            nameof(User), userId,
            $"Deleted email row {emailId}",
            userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
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

    private void SetEmailUnlinkedResult(UnlinkEmailResult result, Guid userId, Guid emailId, Guid actorId)
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

        // Stale page or forged request: fail soft.
        var logins = await userManager.GetLoginsAsync(user);
        var hasLogin = logins.Any(l =>
            string.Equals(l.LoginProvider, provider, StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal));
        if (!hasLogin)
        {
            SetError(localizer["Users_EmailGrid_UnlinkRejected"].Value);
            return RedirectToAction(nameof(Emails));
        }

        // UnlinkAsync keeps the login and email rows in sync and refuses removing the last verified sign-in method.
        // An orphan login falls back to RemoveLoginAsync.
        var rawRows = await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct);
        var matching = rawRows.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.Ordinal)
            && string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal));

        if (matching is not null)
        {
            var result = await userEmailService.UnlinkAsync(user.Id, matching.Id, user.Id, ct);
            SetEmailUnlinkedResult(result, user.Id, matching.Id, user.Id);
        }
        else
        {
            // Orphan login: UnlinkAsync's guard can't see it, so enforce the invariant here. With no verified
            // email this login may be the only way in.
            if (rawRows.Count(r => r.IsVerified) < 1)
            {
                SetError(localizer["Users_LinkedAccounts_UnlinkBlockedLastSignInMethod"].Value);
                return RedirectToAction(nameof(Emails));
            }

            var removeLogin = await userManager.RemoveLoginAsync(user, provider, providerKey);
            if (removeLogin.Succeeded)
            {
                await auditLogService.LogAsync(
                    UsersAuditActions.UserEmailUnlinked,
                    nameof(User), user.Id,
                    $"Unlinked orphan {provider} login (no matching UserEmail row)",
                    user.Id);
                SetSuccess(localizer["Users_EmailGrid_UnlinkSuccess"].Value);
            }
            else
            {
                logger.LogWarning(
                    "UnlinkLinkedAccount: RemoveLoginAsync failed for user {UserId} provider {Provider}: {Errors}",
                    user.Id, provider,
                    string.Join("; ", removeLogin.Errors.Select(e => $"{e.Code}:{e.Description}")));
                SetError(localizer["Users_EmailGrid_UnlinkRejected"].Value);
            }
        }

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
        var result = await userEmailService.SetPrimaryAsync(userId, emailId, ct);
        if (!result.Succeeded)
        {
            logger.LogWarning("Email {Operation} refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                isAdminContext ? nameof(AdminSetPrimary) : nameof(SetPrimary), emailId, userId, actorId, result.ErrorKey);
            SetError(LocalizeEmailRefusal(result.ErrorKey ?? "Common_Error"));
            return;
        }
        // SetPrimaryAsync takes no actor, so the controller audits.
        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailPrimarySet,
            nameof(User), userId,
            isAdminContext ? $"Admin set primary email row {emailId}" : $"Set primary email row {emailId}",
            actorId,
            relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
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

        var result = await userEmailService.AddEmailAsync(id, email, ct);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Admin email addition refused for user {UserId} by {ActorId}: {ErrorKey}",
                id, actor.Id, errorKey);
            SetError(LocalizeEmailRefusal(errorKey));
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        // The address committed: finish its audit and verification queueing even after a disconnect.
        await SendAdminAddedEmailVerificationAsync(id, targetUser, email, result, actor.Id);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SendAdminAddedEmailVerificationAsync(
        Guid userId,
        User targetUser,
        string email,
        AddEmailResult result,
        Guid actorId)
    {
        var trimmedEmail = email.Trim();
        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailAdded,
            nameof(User), userId,
            $"Admin added pending email {trimmedEmail} for user {userId} (conflict: {result.IsConflict})",
            actorId);

        await SendAddedEmailVerificationAsync(targetUser, email, result);
        logger.LogInformation(
            "Admin sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            trimmedEmail, userId, result.IsConflict);

        SetSuccess(localizer["Users_EmailGrid_AdminAddSentVerification"].Value);
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

        var result = await userEmailService.AddVerifiedEmailAsync(userId, email, ct);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Admin verified email addition refused for user {UserId} by {ActorId}: {ErrorKey}",
                userId, actor.Id, errorKey);
            SetError(LocalizeEmailRefusal(errorKey));
        }
        else
        {
            await ReportVerifiedEmailAddAsync(result.Added, userId, email, actor.Id);
        }

        return RedirectToAction(nameof(AdminEmails), new { id = userId });
    }

    private async Task ReportVerifiedEmailAddAsync(
        bool inserted,
        Guid userId,
        string email,
        Guid actorUserId)
    {
        if (!inserted)
        {
            SetInfo($"Email {email} already exists on this user — no change.");
            return;
        }

        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailAdded,
            nameof(User), userId,
            $"Admin added pre-verified email {email} for user {userId} (no verification flow)",
            actorUserId);

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

        var deleted = await userEmailService.DeleteEmailAsync(id, emailId, ct);
        await SetAdminEmailDeletedResultAsync(id, emailId, actor.Id, deleted);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetAdminEmailDeletedResultAsync(Guid userId, Guid emailId, Guid actorId, DeleteEmailResult deleted)
    {
        if (!deleted.Succeeded)
        {
            logger.LogWarning("Admin email deletion refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                emailId, userId, actorId, deleted.ErrorKey);
            SetError(LocalizeEmailRefusal(deleted.ErrorKey ?? "Users_EmailGrid_DeleteRejectedHasProvider"));
            return;
        }

        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailDeleted,
            nameof(User), userId,
            $"Admin deleted email row {emailId}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
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

        var result = await userEmailService.SetVisibilityAsync(id, emailId, visibility, ct);
        if (!result.Succeeded)
        {
            logger.LogWarning("Email AdminSetVisibility refused for email {EmailId}, user {UserId}, actor {ActorId}: {Reason}",
                emailId, id, actor.Id, result.ErrorKey);
            SetError(LocalizeEmailRefusal(result.ErrorKey ?? "Common_Error"));
            return RedirectToAction(nameof(AdminEmails), new { id });
        }
        await SetAdminEmailVisibilityChangedResultAsync(id, emailId, actor.Id, visibility);

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetAdminEmailVisibilityChangedResultAsync(
        Guid userId,
        Guid emailId,
        Guid actorId,
        ContactFieldVisibility? visibility)
    {
        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailVisibilityChanged,
            nameof(User), userId,
            $"Admin changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        SetSuccess(localizer["Users_Profile_EmailVisibilityUpdated"].Value);
    }

    private Task<EmailsViewModel> BuildEmailsViewModelAsync(
        User user, bool isAdminContext = false, CancellationToken ct = default) =>
        emailsViewModelBuilder.BuildAsync(user, isAdminContext, User.IsInRole(RoleNames.Admin), ct);

}
