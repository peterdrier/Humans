using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps.Contracts;

/// <summary>
/// The resource is a <see cref="Humans.Camps.Contracts.CampInfo"/>, the internal <c>Camp</c> entity, or a camp id.
/// </summary>
public sealed class CampOperationRequirement : IAuthorizationRequirement
{
    public static readonly CampOperationRequirement Manage = new(nameof(Manage));

    /// <summary>Lead or Workshop role holder, plus CampAdmin / Admin.</summary>
    public static readonly CampOperationRequirement SubmitEvent = new(nameof(SubmitEvent));

    public string OperationName { get; }

    private CampOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
