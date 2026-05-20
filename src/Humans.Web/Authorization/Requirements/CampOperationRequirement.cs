using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for camp operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, resource, requirement)
/// where the resource is a <see cref="Humans.Application.Interfaces.Camps.CampLookup"/>,
/// a legacy <see cref="Humans.Domain.Entities.Camp"/> entity, or a camp ID (<see cref="System.Guid"/>).
/// </summary>
public sealed class CampOperationRequirement : IAuthorizationRequirement
{
    public static readonly CampOperationRequirement Manage = new(nameof(Manage));

    /// <summary>
    /// Authorizes camp-event submission (<c>EventsController</c>): Lead OR
    /// Workshop role holder, plus CampAdmin / Admin. Resolved through
    /// <see cref="Humans.Application.Interfaces.Camps.ICampService.IsUserCampEventManagerAsync"/>.
    /// </summary>
    public static readonly CampOperationRequirement SubmitEvent = new(nameof(SubmitEvent));

    public string OperationName { get; }

    private CampOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
