using Humans.Store.Services.Dtos;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Store.Authorization;

/// <summary>
/// Resource-based authorization requirement for Store order operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, order, requirement)
/// where the resource is a Order.
/// </summary>
internal sealed class OrderOperationRequirement : IAuthorizationRequirement
{
    public static readonly OrderOperationRequirement View = new(nameof(View));
    public static readonly OrderOperationRequirement Create = new(nameof(Create));
    public static readonly OrderOperationRequirement AddLine = new(nameof(AddLine));
    public static readonly OrderOperationRequirement RemoveLine = new(nameof(RemoveLine));
    public static readonly OrderOperationRequirement EditCounterparty = new(nameof(EditCounterparty));
    /// <summary>Initiate Stripe Checkout to pay against the order. Allowed regardless of order state — payments continue after invoice issuance.</summary>
    public static readonly OrderOperationRequirement Pay = new(nameof(Pay));
    /// <summary>Delete a zero-balance order. Admin-only — camp leads and team coordinators cannot delete their own orders.</summary>
    public static readonly OrderOperationRequirement Delete = new(nameof(Delete));

    public string OperationName { get; }

    private OrderOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}

/// <summary>
/// Resource passed to <see cref="OrderAuthorizationHandler"/> when
/// authorizing a Create-order operation. There is no <c>Order</c> yet, so
/// the camp-lead or team-coordinator check is rooted at the target
/// <c>CampSeason</c> or <c>Team</c>. Exactly one of the two ids is non-null.
/// </summary>
internal sealed record OrderCreateContext(Guid? CampSeasonId, Guid? TeamId = null);

/// <summary>
/// Resource passed to <see cref="OrderAuthorizationHandler"/> when authorizing an
/// AddLine/RemoveLine operation against a specific product: carries the product's
/// <c>OrderableUntil</c> so the handler can deny non-admin line edits past the order
/// deadline. Store admins are exempt (the admin path succeeds before the deadline gate).
/// </summary>
internal sealed record OrderLineContext(OrderDto Order, LocalDate ProductOrderableUntil);
