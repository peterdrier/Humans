using System.Globalization;
using Humans.Base.Attributes;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Humans.Users.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Users.Controllers;

/// <summary>
/// Where an emailed confirmation link lands. Opening the link changes nothing (mail scanners open links);
/// only a button press by the signed-in account that asked confirms an address or merges two accounts.
/// </summary>
[Authorize]
[Route("Users/Emails")]
internal sealed class UserEmailConfirmationController(
    IUserManagement userService,
    EmailConfirmationService confirmation,
    MergeAccountCardBuilder cards,
    IUserSignInService signIn,
    IStringLocalizer<UsersResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("Confirm")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Confirm(string? token, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        return await PageAsync(token, user.Id, await confirmation.ReadAsync(token, user.Id, ct), choiceMissing: false, ct);
    }

    [HttpPost("Confirm")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> ConfirmAddress(string? token, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        // Once pressed, finish even if the browser goes away.
        var result = await confirmation.ConfirmAsync(token, user.Id, CancellationToken.None);
        if (result.State != EmailConfirmationState.Confirm)
            return await PageAsync(token, user.Id, result, choiceMissing: false, ct);

        SetSuccess(string.Format(CultureInfo.CurrentCulture, localizer["Users_Profile_EmailVerified"].Value, result.Email));
        return RedirectToAction(nameof(ProfileEmailsController.Emails), "ProfileEmails");
    }

    [HttpPost("Merge")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Merge(string? token, Guid? keepUserId, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var outcome = await confirmation.MergeAsync(token, user.Id, keepUserId, CancellationToken.None);
        if (outcome.KeptUserId is not { } keptUserId)
            return await PageAsync(
                token, user.Id,
                outcome.ChoiceMissing
                    ? await confirmation.ReadAsync(token, user.Id, ct)
                    : new EmailConfirmation(outcome.State),
                outcome.ChoiceMissing, ct);

        // The other account was kept: this session's account no longer exists.
        if (keptUserId != user.Id)
            await signIn.SignInAsync(keptUserId);

        SetSuccess(localizer["Users_MergeAccounts_Done"].Value);
        return RedirectToAction(nameof(ProfileEmailsController.Emails), "ProfileEmails");
    }

    private async Task<IActionResult> PageAsync(
        string? token, Guid signedInUserId, EmailConfirmation state, bool choiceMissing, CancellationToken ct)
    {
        var merging = state is { State: EmailConfirmationState.Merge, OtherUserId: not null };
        var mine = merging ? await cards.BuildAsync(signedInUserId, isSignedIn: true, ct) : null;
        var other = merging ? await cards.BuildAsync(state.OtherUserId!.Value, isSignedIn: false, ct) : null;
        foreach (var card in new[] { mine, other })
            card?.MustChoose = state.OtherHasNames;

        return View("Confirm", new EmailConfirmViewModel
        {
            State = state.State,
            Token = token ?? string.Empty,
            Email = state.Email,
            AskingUserId = state.AskingUserId,
            SignedInUserId = signedInUserId,
            Mine = mine,
            Other = other,
            MustChoose = state.OtherHasNames,
            ChoiceMissing = choiceMissing,
        });
    }
}
