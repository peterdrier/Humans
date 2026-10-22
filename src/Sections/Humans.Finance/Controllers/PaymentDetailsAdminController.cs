using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Finance.Services;
using Humans.Finance.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Finance.Controllers;

/// <summary>Admin only, as the card is: Board and FinanceAdmin see neither.</summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Finance/Admin/Members")]
internal sealed class PaymentDetailsAdminController(
    IUserService userService,
    IPaymentDetailsManagement paymentDetails) : HumansControllerBase(userService)
{
    /// <summary>Hands the unmasked IBAN to the card's next render through TempData, audited.</summary>
    [HttpPost("{id:guid}/RevealIban")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealIban(Guid id, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorUserId) return Forbid();

        var iban = await paymentDetails.RevealIbanAsync(id, actorUserId, ct);
        if (iban is null)
            SetError("No IBAN on record for this user.");
        else
            TempData[PaymentDetailsViewComponent.RevealedIbanKey] = iban;

        return RedirectToAction("AdminDetail", "UsersAdmin", new { id });
    }
}
