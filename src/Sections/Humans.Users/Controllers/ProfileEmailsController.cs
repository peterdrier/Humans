using Humans.Users.Services;
using Humans.Base.Attributes;
using Humans.Base.Models.Tables;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Controllers;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using AngleSharp.Dom;
using Humans.Users.Authorization;
using Humans.Base.Configuration;
using Microsoft.Extensions.Configuration;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Gdpr.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Users.Models;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Camps.Contracts;
using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Base;
using Humans.Base.Authorization;

using Humans.GoogleIntegration.Contracts;

namespace Humans.Users.Controllers;

// Email addresses and linked logins: the member's own grid under /Profile/Me/Emails and the
// admin grid under /Profile/{id}/Admin/Emails. Split out of ProfileController, which keeps
// the own-profile pages; ProfileViewController holds other members' profiles.
[Authorize]
[Route("Profile")]
internal sealed class ProfileEmailsController(
    IUserServiceInternal userService,
    UserManager<User> userManager,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IUserEmailService userEmailService,
    IAuditLogService auditLogService,
    ILogger<ProfileEmailsController> logger,
    IStringLocalizer<UsersResource> localizer,
    ITicketServiceRead ticketQueryService,
    IAuthorizationService authorizationService,
    SignInManager<User> signInManager,
    IOptions<GoogleWorkspaceOptions> googleWorkspaceOptions) : HumansControllerBase(userService)
{
    private readonly ITicketServiceRead _ticketQueryService = ticketQueryService;
    private readonly IUserServiceInternal _userService = userService;
    private readonly GoogleWorkspaceOptions _googleWorkspaceOptions = googleWorkspaceOptions.Value;

    // ─── Own Emails ──────────────────────────────────────────────────

    [HttpGet("Me/Emails")]
    public async Task<IActionResult> Emails()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(user);
        return View(viewModel);
    }

    [HttpPost("Me/Emails/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEmail(EmailsViewModel model)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(model.NewEmail) || !ModelState.IsValid)
        {
            if (string.IsNullOrWhiteSpace(model.NewEmail))
                ModelState.AddModelError(nameof(model.NewEmail), localizer["Profile_EnterEmail"].Value);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        try
        {
            var result = await userEmailService.AddEmailAsync(user.Id, model.NewEmail);
            await SendAddedEmailVerificationAsync(user, model.NewEmail, result);
            SetAddedEmailFlash(model.NewEmail, result.IsConflict);
        }
        catch (ValidationException ex)
        {
            logger.LogWarning(
                "Rejected email add for user {UserId} ({Email}): {Reason}",
                user.Id, model.NewEmail, ex.Message);
            ModelState.AddModelError(nameof(model.NewEmail), ex.Message);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

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

        await emailService.SendAsync(emailMessages.EmailVerification(
            trimmedEmail,
            info?.BurnerName ?? string.Empty,
            verificationUrl!,
            result.IsConflict,
            user.PreferredLanguage));

        logger.LogInformation(
            "Sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            trimmedEmail, user.Id, result.IsConflict);
    }

    private void SetAddedEmailFlash(string email, bool isConflict)
    {
        if (isConflict)
        {
            SetInfo("This email is linked to another account. Verifying it will request an account merge. Check your inbox for the verification link.");
            return;
        }

        SetSuccess(string.Format(CultureInfo.CurrentCulture, localizer["Profile_VerificationSent"].Value, email.Trim()));
    }

    [HttpGet("Me/Emails/Verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail(Guid userId, Guid emailId, string token)
    {
        if (string.IsNullOrEmpty(token) || emailId == Guid.Empty)
        {
            return VerifyEmailError(localizer["Profile_InvalidVerificationLink"].Value);
        }

        try
        {
            var decodedToken = HttpUtility.UrlDecode(token);
            var result = await userEmailService.VerifyEmailAsync(userId, emailId, decodedToken);

            return VerifyEmailSuccess(userId, result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogInformation("Email verification failed for user {UserId}: {Message}", userId, ex.Message);
            return VerifyEmailError(localizer["Profile_InvalidVerificationLink"].Value);
        }
        catch (ValidationException ex)
        {
            logger.LogInformation("Email verification validation failed for user {UserId}: {Message}", userId, ex.Message);
            return VerifyEmailError(ex.Message);
        }
    }

    private IActionResult VerifyEmailSuccess(Guid userId, VerifyEmailResult result)
    {
        if (result.MergeRequestCreated)
        {
            logger.LogInformation(
                "User {UserId} verified email {Email} - merge request created",
                userId, result.Email);

            ViewData["Success"] = true;
            ViewData["Message"] = $"Email verified. A merge request has been submitted for admin review. The email {result.Email} will be added to your account once approved.";
            return View("VerifyEmailResult");
        }

        logger.LogInformation(
            "User {UserId} verified email {Email}",
            userId, result.Email);

        ViewData["Success"] = true;
        ViewData["Message"] = string.Format(localizer["Profile_EmailVerified"].Value, result.Email);
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
    public async Task<IActionResult> SetPrimary(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            await userEmailService.SetPrimaryAsync(user.Id, emailId, ct);
            // Self audit at controller — SetPrimaryAsync doesn't take actorUserId.
            await auditLogService.LogAsync(
                AuditAction.UserEmailPrimarySet,
                nameof(User), user.Id,
                $"Set primary email row {emailId}",
                user.Id,
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
            SetSuccess(localizer["Profile_NotificationTargetUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to set primary email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost("Me/Emails/SetVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEmailVisibility(Guid emailId, string? visibility)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var parsedVisibility = ParseEmailVisibility(visibility);

        try
        {
            await userEmailService.SetVisibilityAsync(user.Id, emailId, parsedVisibility);
            await LogSelfEmailVisibilityChangedAsync(user.Id, emailId, parsedVisibility);
            SetSuccess(localizer["Profile_EmailVisibilityUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to set email visibility for email {EmailId} and user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private static ContactFieldVisibility? ParseEmailVisibility(string? visibility) =>
        !string.IsNullOrEmpty(visibility) && Enum.TryParse<ContactFieldVisibility>(visibility, ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private Task LogSelfEmailVisibilityChangedAsync(
        Guid userId,
        Guid emailId,
        ContactFieldVisibility? visibility) =>
        auditLogService.LogAsync(
            AuditAction.UserEmailVisibilityChanged,
            nameof(User), userId,
            $"Changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}",
            userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
    [HttpPost("Me/Emails/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEmail(Guid emailId)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var deleted = await userEmailService.DeleteEmailAsync(user.Id, emailId);
            if (deleted)
            {
                await LogSelfEmailDeletedAsync(user.Id, emailId);
                SetSuccess(localizer["Profile_EmailDeleted"].Value);
            }
            else
            {
                SetError(localizer["EmailGrid_DeleteRejectedHasProvider"].Value);
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to delete email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private Task LogSelfEmailDeletedAsync(Guid userId, Guid emailId) =>
        auditLogService.LogAsync(
            AuditAction.UserEmailDeleted,
            nameof(User), userId,
            $"Deleted email row {emailId}",
            userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
    [HttpPost("Me/Emails/SetGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGoogle(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.SetGoogleAsync(user.Id, emailId, user.Id, ct);
            SetGoogleEmailResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to set Google service email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetGoogleEmailResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_GoogleServiceUpdated"].Value);
            return;
        }

        SetError(localizer["EmailGrid_SetGoogleRejected"].Value);
    }

    [HttpPost("Me/Emails/ClearGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearGoogle(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.ClearGoogleAsync(user.Id, emailId, user.Id, ct);
            SetGoogleEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to clear Google flag on email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetGoogleEmailClearedResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_GoogleFlagCleared"].Value);
            return;
        }

        SetError(localizer["EmailGrid_ClearGoogleRejected"].Value);
    }

    [HttpPost("Me/Emails/ClearPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearPrimary(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.ClearPrimaryAsync(user.Id, emailId, user.Id, ct);
            SetPrimaryEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to clear primary flag on email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetPrimaryEmailClearedResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_PrimaryFlagCleared"].Value);
            return;
        }

        SetError(localizer["EmailGrid_ClearPrimaryRejected"].Value);
    }

    [HttpPost("Me/Emails/Link/{provider}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Link(string provider, string? returnUrl = null)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        // Round-trip via ExternalLoginCallback so link-while-signed-in branch fires.
        var resolvedReturnUrl = returnUrl ?? Url.Action(nameof(Emails)) ?? "/Profile/Me/Emails";
        var redirectUrl = Url.Action("ExternalLoginCallback", "Account", new { returnUrl = resolvedReturnUrl })
            ?? "/Account/ExternalLoginCallback";
        var props = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(props, provider);
    }

    [HttpPost("Me/Emails/Unlink/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlink(Guid id, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.UnlinkAsync(user.Id, id, user.Id, ct);
            SetEmailUnlinkedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to unlink email {EmailId} for user {UserId}", id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetEmailUnlinkedResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_UnlinkSuccess"].Value);
            return;
        }

        SetError(localizer["EmailGrid_UnlinkRejected"].Value);
    }

    // User-facing Unlink (see nobodies-collective/Humans#731) — keyed by (Provider, ProviderKey); enforces auth-method invariant server-side.
    [HttpPost("Me/LinkedAccounts/Unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkLinkedAccount(string provider, string providerKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerKey))
        {
            SetError(localizer["EmailGrid_UnlinkRejected"].Value);
            return RedirectToAction(nameof(Emails));
        }

        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        // Stale dashboard or forged request — fail soft.
        var logins = await userManager.GetLoginsAsync(user);
        var hasLogin = logins.Any(l =>
            string.Equals(l.LoginProvider, provider, StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal));
        if (!hasLogin)
        {
            SetError(localizer["EmailGrid_UnlinkRejected"].Value);
            return RedirectToAction(nameof(Emails));
        }

        // Route via UnlinkAsync to keep AspNetUserLogins + user_emails in sync. Orphan logins fall back to RemoveLoginAsync.
        // The last-verified-sign-in-method invariant is enforced inside UnlinkAsync (ValidationException).
        var rawRows = await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct);
        var matching = rawRows.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.Ordinal)
            && string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal));

        try
        {
            if (matching is not null)
            {
                var ok = await userEmailService.UnlinkAsync(user.Id, matching.Id, user.Id, ct);
                SetEmailUnlinkedResult(ok);
            }
            else
            {
                // Orphan login: no UserEmail row — drop directly. UnlinkAsync's guard can't
                // see this branch, so enforce the auth-method invariant here: with zero
                // verified emails this login may be the user's only sign-in path.
                if (rawRows.Count(r => r.IsVerified) < 1)
                {
                    SetError(localizer["LinkedAccounts_UnlinkBlockedLastSignInMethod"].Value);
                    return RedirectToAction(nameof(Emails));
                }

                var removeLogin = await userManager.RemoveLoginAsync(user, provider, providerKey);
                if (removeLogin.Succeeded)
                {
                    await auditLogService.LogAsync(
                        AuditAction.UserEmailUnlinked,
                        nameof(User), user.Id,
                        $"Unlinked orphan {provider} login (no matching UserEmail row)",
                        user.Id);
                    SetSuccess(localizer["EmailGrid_UnlinkSuccess"].Value);
                }
                else
                {
                    logger.LogWarning(
                        "UnlinkLinkedAccount: RemoveLoginAsync failed for user {UserId} provider {Provider}: {Errors}",
                        user.Id, provider,
                        string.Join("; ", removeLogin.Errors.Select(e => $"{e.Code}:{e.Description}")));
                    SetError(localizer["EmailGrid_UnlinkRejected"].Value);
                }
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
                "Failed to unlink provider {Provider} for user {UserId}: {Reason}",
                provider, user.Id, ex.Message);
            // UnlinkAsync's auth-method guard is the only ValidationException source here —
            // surface it via the localized string instead of the English exception message.
            SetError(ex is ValidationException
                ? localizer["LinkedAccounts_UnlinkBlockedLastSignInMethod"].Value
                : ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }
    // Admin grid mirrors self-grid against a target user. No AdminLink: OAuth linking requires target's authentication.

    [HttpGet("{id:guid}/Admin/Emails")]
    public async Task<IActionResult> AdminEmails(Guid id, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var targetUser = await userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(targetUser, isAdminContext: true, ct);
        return View("Emails", viewModel);
    }

    [HttpPost("{id:guid}/Admin/Emails/SetGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetGoogle(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await userEmailService.SetGoogleAsync(id, emailId, actor.Id, ct);
            SetGoogleEmailResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to set Google service email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
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

        try
        {
            await userEmailService.SetPrimaryAsync(id, emailId, ct);
            // Audit at controller — SetPrimaryAsync has no actorUserId.
            await auditLogService.LogAsync(
                AuditAction.UserEmailPrimarySet,
                nameof(User), id,
                $"Admin set primary email row {emailId}",
                actor.Id,
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
            SetSuccess(localizer["Profile_NotificationTargetUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to set primary email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/ClearGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminClearGoogle(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await userEmailService.ClearGoogleAsync(id, emailId, actor.Id, ct);
            SetGoogleEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to clear Google flag on email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
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

        try
        {
            var ok = await userEmailService.ClearPrimaryAsync(id, emailId, actor.Id, ct);
            SetPrimaryEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to clear primary flag on email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

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
            SetError(localizer["Profile_EnterEmail"].Value);
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var targetUser = await userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        try
        {
            var result = await userEmailService.AddEmailAsync(id, email, ct);
            await SendAdminAddedEmailVerificationAsync(id, targetUser, email, result, actor.Id, ct);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
                "Admin failed to add email for user {UserId} ({Email}): {Reason}",
                id, email, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SendAdminAddedEmailVerificationAsync(
        Guid userId,
        User targetUser,
        string email,
        AddEmailResult result,
        Guid actorId,
        CancellationToken ct)
    {
        var trimmedEmail = email.Trim();
        var verificationUrl = Url.Action(
            nameof(VerifyEmail),
            "ProfileEmails",
            new { userId, emailId = result.EmailId, token = HttpUtility.UrlEncode(result.Token) },
            Request.Scheme);

        var info = await _userService.GetUserInfoAsync(userId, ct);

        await emailService.SendAsync(emailMessages.EmailVerification(
            trimmedEmail,
            info?.BurnerName ?? string.Empty,
            verificationUrl!,
            result.IsConflict,
            targetUser.PreferredLanguage),
            ct);

        logger.LogInformation(
            "Admin sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            trimmedEmail, userId, result.IsConflict);

        await auditLogService.LogAsync(
            AuditAction.UserEmailAdded,
            nameof(User), userId,
            $"Admin added pending email {trimmedEmail} for user {userId} (conflict: {result.IsConflict})",
            actorId);

        SetSuccess(localizer["EmailGrid_AdminAddSentVerification"].Value);
    }
    // Admin recovery: insert a verified UserEmail without verification email.
    [HttpPost("{id:guid}/Admin/Emails/AddVerified")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminAddVerifiedEmail(Guid id, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(localizer["Profile_EnterEmail"].Value);
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

        try
        {
            var inserted = await userEmailService.AddVerifiedEmailAsync(userId, email, ct);
            await ReportVerifiedEmailAddAsync(inserted, userId, email, actor.Id);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
                "Admin failed to add verified email for user {UserId} ({Email}): {Reason}",
                userId, email, ex.Message);
            SetError(ex.Message);
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
            AuditAction.UserEmailAdded,
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

        try
        {
            var result = await userEmailService.AdminMarkVerifiedAsync(id, emailId, actor.Id, ct);
            if (result.MergeRequestCreated)
            {
                SetSuccess(localizer["EmailGrid_AdminVerifyMergeRequested"].Value);
            }
            else
            {
                SetSuccess(localizer["EmailGrid_AdminVerifySuccess"].Value);
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
                "Admin failed to manually verify email {EmailId} for user {UserId}: {Reason}",
                emailId, id, ex.Message);
            SetError(ex.Message);
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

        try
        {
            var ok = await userEmailService.UnlinkAsync(id, emailId, actor.Id, ct);
            SetEmailUnlinkedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to unlink email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

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

        try
        {
            var deleted = await userEmailService.DeleteEmailAsync(id, emailId, ct);
            await SetAdminEmailDeletedResultAsync(id, emailId, actor.Id, deleted);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to delete email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetAdminEmailDeletedResultAsync(Guid userId, Guid emailId, Guid actorId, bool deleted)
    {
        if (!deleted)
        {
            SetError(localizer["EmailGrid_DeleteRejectedHasProvider"].Value);
            return;
        }

        await auditLogService.LogAsync(
            AuditAction.UserEmailDeleted,
            nameof(User), userId,
            $"Admin deleted email row {emailId}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        SetSuccess(localizer["Profile_EmailDeleted"].Value);
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

        try
        {
            await userEmailService.SetVisibilityAsync(id, emailId, visibility, ct);
            await SetAdminEmailVisibilityChangedResultAsync(id, emailId, actor.Id, visibility);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to set email visibility for email {EmailId} and user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetAdminEmailVisibilityChangedResultAsync(
        Guid userId,
        Guid emailId,
        Guid actorId,
        ContactFieldVisibility? visibility)
    {
        await auditLogService.LogAsync(
            AuditAction.UserEmailVisibilityChanged,
            nameof(User), userId,
            $"Admin changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        SetSuccess(localizer["Profile_EmailVisibilityUpdated"].Value);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private async Task<EmailsViewModel> BuildEmailsViewModelAsync(User user, bool isAdminContext = false, CancellationToken ct = default)
    {
        var emails = await userEmailService.GetUserEmailsAsync(user.Id, ct);
        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        var burnerName = info?.BurnerName ?? string.Empty;

        var (canAdd, minutesUntilResend) = await GetEmailAddCooldownStatusAsync(emails, ct);

        var hasNobodiesTeam = emails.Any(e => e.IsVerified &&
            e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase));

        // Use the already-loaded `emails` — UserManager doesn't .Include(UserEmails).
        var googleServiceEmail = emails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault();

        var workspaceLockedEmail = FindWorkspaceLockedEmail(emails);

        var (userLogins, rawUserEmails) = await GetAdminEmailDiagnosticsAsync(user.Id, isAdminContext, ct);

        var linkedAccounts = await BuildLinkedAccountsAsync(user, emails, isAdminContext, ct);

        // see nobodies-collective/Humans#758 — addresses linked to the user's event ticket.
        // The grid hides Delete for these rows; UserEmailService.DeleteEmailAsync re-validates.
        var ticketEmails = await GetTicketLinkedEmailsAsync(user.Id, ct);

        bool RowIsTicketLinked(string address) =>
            ticketEmails.Any(ticketEmail => Base.Helpers.EmailNormalization.EmailsMatch(address, ticketEmail));

        bool RowHasOrphanProviderTag(string? provider, string? providerKey) =>
            isAdminContext
            && !string.IsNullOrEmpty(provider)
            && !string.IsNullOrEmpty(providerKey)
            && !userLogins.Any(l =>
                string.Equals(l.Provider, provider, StringComparison.Ordinal)
                && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal));

        bool LoginHasOrphanRow(string provider, string providerKey) =>
            !emails.Any(e =>
                string.Equals(e.Provider, provider, StringComparison.Ordinal)
                && string.Equals(e.ProviderKey, providerKey, StringComparison.Ordinal));

        return new EmailsViewModel
        {
            Emails = emails.Select(e => new EmailRowViewModel
            {
                Id = e.Id,
                Email = e.Email,
                IsVerified = e.IsVerified,
                IsGoogle = e.IsGoogle,
                GoogleEmailStatus = e.GoogleEmailStatus,
                IsPrimary = e.IsPrimary,
                Visibility = e.Visibility,
                IsPendingVerification = e.IsPendingVerification,
                IsMergePending = e.IsMergePending,
                IsNobodiesTeamDomain = e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase),
                Provider = e.Provider,
                HasOrphanProviderTag = RowHasOrphanProviderTag(e.Provider, e.ProviderKey),
                IsTicketLinked = RowIsTicketLinked(e.Email),
            }).ToList(),
            ExternalLogins = userLogins.Select(l => new ExternalLoginRowViewModel
            {
                LoginProvider = l.Provider,
                ProviderKeyHash = HashForDisplay(l.ProviderKey),
                ProviderDisplayName = null,
                HasOrphanLogin = LoginHasOrphanRow(l.Provider, l.ProviderKey),
            }).ToList(),
            RawUserEmails = rawUserEmails,
            LinkedAccounts = linkedAccounts,
            CanAddEmail = canAdd,
            MinutesUntilResend = minutesUntilResend,
            GoogleServiceEmail = googleServiceEmail,
            HasNobodiesTeamEmail = hasNobodiesTeam,
#pragma warning disable CS0618 // Legacy User.GoogleEmailStatus remains mapped for compatibility.
            GoogleEmailStatus = user.GoogleEmailStatus,
#pragma warning restore CS0618
            TargetUserId = user.Id,
            TargetDisplayName = burnerName,
            IsAdminContext = isAdminContext,
            WorkspaceLockedEmailId = workspaceLockedEmail?.Id,
            LegacyIdentityEmailColumn = isAdminContext
                && User.IsInRole(RoleNames.Admin)
                ? user.IdentityEmailColumn
                : null,
            TargetUserInfo = isAdminContext
                && User.IsInRole(RoleNames.Admin)
                    ? info
                    : null,
        };
    }

    private async Task<(bool CanAdd, int MinutesUntilResend)> GetEmailAddCooldownStatusAsync(
        IReadOnlyList<UserEmailEditDto> emails, CancellationToken ct)
    {
        var pendingEmail = emails.FirstOrDefault(e => e.IsPendingVerification);
        if (pendingEmail is null)
            return (true, 0);

        var (cooldownCanAdd, cooldownMinutes, _) =
            await userEmailService.GetEmailCooldownInfoAsync(pendingEmail.Id, ct);
        return (cooldownCanAdd, cooldownMinutes);
    }

    // Workspace canonical: Provider=Google + Workspace-domain email. Locks Primary + Google radios.
    private UserEmailEditDto? FindWorkspaceLockedEmail(IReadOnlyList<UserEmailEditDto> emails)
    {
        var workspaceDomainSuffix = "@" + _googleWorkspaceOptions.Domain;
        var workspaceCandidates = emails
            .Where(e => !string.IsNullOrEmpty(e.Provider)
                && string.Equals(e.Provider, "Google", StringComparison.OrdinalIgnoreCase)
                && e.Email.EndsWith(workspaceDomainSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return workspaceCandidates.FirstOrDefault(e => e.IsPrimary)
            ?? workspaceCandidates.FirstOrDefault();
    }

    // see nobodies-collective/Humans#697 — admin diagnostic loads AspNetUserLogins + computes store-disagreement.
    private async Task<(IReadOnlyList<(string Provider, string ProviderKey)> UserLogins, IReadOnlyList<UserEmailRowSnapshot> RawUserEmails)>
        GetAdminEmailDiagnosticsAsync(Guid userId, bool isAdminContext, CancellationToken ct)
    {
        IReadOnlyList<(string Provider, string ProviderKey)> userLogins = [];
        IReadOnlyList<UserEmailRowSnapshot> rawUserEmails = [];
        if (!isAdminContext)
            return (userLogins, rawUserEmails);

        var loginsByUser = await _userService.GetExternalLoginsByUserIdsAsync([userId], ct);
        if (loginsByUser.TryGetValue(userId, out var list))
            userLogins = list;
        rawUserEmails = await userEmailService.GetEntitiesByUserIdAsync(userId, ct);
        return (userLogins, rawUserEmails);
    }

    // see nobodies-collective/Humans#731 — self uses UserManager (ProviderDisplayName); stitches UserEmail row id + CreatedAt.
    private async Task<IReadOnlyList<LinkedOAuthAccountViewModel>> BuildLinkedAccountsAsync(
        User user, IReadOnlyList<UserEmailEditDto> emails, bool isAdminContext, CancellationToken ct)
    {
        if (isAdminContext)
            return [];

        var logins = await userManager.GetLoginsAsync(user);
        if (logins.Count == 0)
            return [];

        // (Provider, ProviderKey) uniqueness is service-enforced, not DB-enforced — keep first row per key.
        var rowsByKey = new Dictionary<(string, string), UserEmailRowSnapshot>();
        foreach (var r in await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct))
        {
            if (string.IsNullOrEmpty(r.Provider) || string.IsNullOrEmpty(r.ProviderKey))
                continue;
            rowsByKey.TryAdd((r.Provider!, r.ProviderKey!), r);
        }

        // Auth-method invariant: at least one verified row must remain post-unlink (orphan logins don't touch rows).
        var verifiedTotal = emails.Count(e => e.IsVerified);

        return logins.Select(l =>
        {
            rowsByKey.TryGetValue((l.LoginProvider, l.ProviderKey), out var row);
            var rowIsVerified = row?.IsVerified == true;
            var verifiedAfter = verifiedTotal - (rowIsVerified ? 1 : 0);
            return new LinkedOAuthAccountViewModel
            {
                Provider = l.LoginProvider,
                ProviderKey = l.ProviderKey,
                ProviderDisplayName = l.ProviderDisplayName,
                ProviderKeyHash = HashForDisplay(l.ProviderKey),
                MatchingUserEmailId = row?.Id,
                Email = row?.Email,
                LinkedAt = row?.CreatedAt,
                CanUnlink = verifiedAfter >= 1,
            };
        }).ToList();
    }

    private async Task<List<string>> GetTicketLinkedEmailsAsync(Guid userId, CancellationToken ct)
    {
        var ticketOrders = await _ticketQueryService.GetTicketOrdersAsync(ct);
        return ticketOrders
            .Where(o => o.MatchedUserId == userId && !string.IsNullOrWhiteSpace(o.BuyerEmail))
            .Select(o => o.BuyerEmail!)
            .Concat(ticketOrders
                .SelectMany(o => o.Attendees)
                .Where(a => a.MatchedUserId == userId && !string.IsNullOrWhiteSpace(a.AttendeeEmail))
                .Select(a => a.AttendeeEmail!))
            .ToList();
    }

    private static string HashForDisplay(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

}
