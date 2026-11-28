using Humans.Store.Services.Dtos;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Store.Authorization;

/// <summary>The resource is an <c>OrderDto</c>, <c>OrderCreateContext</c> or <c>OrderLineContext</c>.</summary>
internal sealed class OrderOperationRequirement : IAuthorizationRequirement
{
    public static readonly OrderOperationRequirement View = new();
    public static readonly OrderOperationRequirement Create = new();
    public static readonly OrderOperationRequirement AddLine = new();
    public static readonly OrderOperationRequirement RemoveLine = new();
    public static readonly OrderOperationRequirement EditCounterparty = new();
    /// <summary>Allowed in any order state: payments continue after invoice issuance.</summary>
    public static readonly OrderOperationRequirement Pay = new();
    /// <summary>Camp leads and team coordinators can't delete their own orders.</summary>
    public static readonly OrderOperationRequirement Delete = new();
    public static readonly OrderOperationRequirement IssueInvoice = new();
    public static readonly OrderOperationRequirement RecordPayment = new();
    /// <summary>Narrower than <see cref="RecordPayment"/>: bank transfers and refunds, Finance admin and Admin only.</summary>
    public static readonly OrderOperationRequirement RecordFinancePayment = new();
    public static readonly OrderOperationRequirement DeletePayment = new();

    private OrderOperationRequirement()
    {
    }
}

/// <summary>No order exists yet, so the check is rooted at the target camp season or team. Exactly one id is non-null.</summary>
internal sealed record OrderCreateContext(Guid? CampSeasonId, Guid? TeamId = null);

/// <summary>Carries the product's deadline so the handler can deny non-admin line edits past it.</summary>
internal sealed record OrderLineContext(OrderDto Order, LocalDate ProductOrderableUntil);
