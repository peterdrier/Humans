using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.Finance.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Finance.ViewComponents;

/// <summary>
/// The host page also admits Board and HumanAdmin, so the component checks for Admin itself. The unmasked IBAN shows
/// for one render after <c>PaymentDetailsAdminController.RevealIban</c>.
/// </summary>
public sealed class PaymentDetailsViewComponent(
    IFinanceService finance,
    IAuthorizationService authorization) : ViewComponent
{
    internal const string RevealedIbanKey = "RevealedIban";

    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.AdminOnly)).Succeeded)
            return Content(string.Empty);

        var iban = await finance.GetIbanAsync(userId, HttpContext.RequestAborted);
        var revealed = TempData.TryGetValue(RevealedIbanKey, out var value) ? value as string : null;
        return View(new PaymentDetailsViewModel(
            userId,
            iban is null ? null : IbanFormatter.Mask(iban),
            iban is null ? null : revealed));
    }
}

internal sealed record PaymentDetailsViewModel(Guid UserId, string? MaskedIban, string? RevealedIban);
