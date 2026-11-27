using Humans.Base.Interfaces;

namespace Humans.Tasks.Contracts;

/// <summary>The one write another section calls: loading a whole set of tasks. Everything else Tasks does is internal.</summary>
public interface ITaskWrite : IApplicationService
{
    /// <summary>
    /// Creates every task in the payload, or none. The whole payload is checked first and every problem is listed; a
    /// dry run stops there. Auth-free: the caller has already checked <c>Tasks.Manage</c> on every team in the payload.
    /// Loading the same payload twice creates the tasks twice.
    /// </summary>
    Task<BulkLoadResult> BulkLoadAsync(BulkLoadRequest request, Guid actorUserId, CancellationToken ct = default);
}
