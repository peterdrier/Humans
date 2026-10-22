using Humans.Store.Services.Dtos;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Store.Authorization;

/// <summary>The resource is an <c>OrderDto</c>, <c>OrderCreateContext</c> or <c>OrderLineContext</c>.</summary>
internal sealed class OrderOperationRequirement : IAuthorizationRequirement
{
    public static readonly OrderOperationRequirement View = new(nameof(View));
    public static readonly OrderOperationRequirement Create = new(nameof(Create));
    public static readonly OrderOperationRequirement AddLine = new(nameof(AddLine));
    public static readonly OrderOperationRequirement RemoveLine = new(nameof(RemoveLine));
    public static readonly OrderOperationRequirement EditCounterparty = new(nameof(EditCounterparty));
    /// <summary>Allowed in any order state: payments continue after invoice issuance.</summary>
    public static readonly OrderOperationRequirement Pay = new(nameof(Pay));
    /// <summary>Camp leads and team coordinators can't delete their own orders.</summary>
    public static readonly OrderOperationRequirement Delete = new(nameof(Delete));
    public static readonly OrderOperationRequirement IssueInvoice = new(nameof(IssueInvoice));
    public static readonly OrderOperationRequirement RecordPayment = new(nameof(RecordPayment));
    /// <summary>Narrower than <see cref="RecordPayment"/>: Finance admin and Admin only.</summary>
    public static readonly OrderOperationRequirement Refund = new(nameof(Refund));
    public static readonly OrderOperationRequirement DeletePayment = new(nameof(DeletePayment));

    public string OperationName { get; }

    private OrderOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}

/// <summary>No order exists yet, so the check is rooted at the target camp season or team. Exactly one id is non-null.</summary>
internal sealed record OrderCreateContext(Guid? CampSeasonId, Guid? TeamId = null);

/// <summary>Carries the product's deadline so the handler can deny non-admin line edits past it.</summary>
internal sealed record OrderLineContext(OrderDto Order, LocalDate ProductOrderableUntil);
