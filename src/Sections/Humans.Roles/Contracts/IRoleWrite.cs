using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Roles.Contracts;

/// <summary>The one Roles write another section calls.</summary>
public interface IRoleWrite : IApplicationService
{
    /// <summary>
    /// Takes the person out of every role on the owner, audited as <paramref name="actorUserId"/>. Owner sections call
    /// it before ending a membership. Does nothing when they hold none.
    /// </summary>
    Task RemoveAsync(Owner owner, Guid userId, Guid actorUserId, CancellationToken ct = default);
}
