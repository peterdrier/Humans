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
    public static readonly StoreOrderOperationRequirement AddLine = new(nameof(AddLine));
    public static readonly StoreOrderOperationRequirement RemoveLine = new(nameof(RemoveLine));
    public static readonly StoreOrderOperationRequirement EditCounterparty = new(nameof(EditCounterparty));

    public string OperationName { get; }

    private StoreOrderOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
