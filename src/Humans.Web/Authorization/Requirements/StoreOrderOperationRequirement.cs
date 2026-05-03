using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for Store order operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, order, requirement)
/// where the resource is a StoreOrder.
/// </summary>
public sealed class StoreOrderOperationRequirement : IAuthorizationRequirement
{
    public static readonly StoreOrderOperationRequirement View = new(nameof(View));
    public static readonly StoreOrderOperationRequirement Create = new(nameof(Create));
    public static readonly StoreOrderOperationRequirement AddLine = new(nameof(AddLine));
    public static readonly StoreOrderOperationRequirement RemoveLine = new(nameof(RemoveLine));
    public static readonly StoreOrderOperationRequirement EditCounterparty = new(nameof(EditCounterparty));
    /// <summary>Initiate Stripe Checkout to pay against the order. Allowed regardless of order state — payments continue after invoice issuance.</summary>
    public static readonly StoreOrderOperationRequirement Pay = new(nameof(Pay));

    public string OperationName { get; }

    private StoreOrderOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}

/// <summary>
/// Resource passed to <see cref="StoreOrderAuthorizationHandler"/> when
/// authorizing a Create-order operation. There is no <c>StoreOrder</c> yet, so
/// the camp-lead check is rooted at the target <c>CampSeason</c>.
/// </summary>
public sealed record StoreOrderCreateContext(Guid CampSeasonId);
