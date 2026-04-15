using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Stores;

/// <summary>
/// In-memory canonical store for the Governance aggregate
/// (<see cref="MemberApplication"/>). See
/// <c>docs/architecture/design-rules.md</c> §4 for the store pattern.
/// </summary>
/// <remarks>
/// Warmed at startup via <c>IApplicationRepository.GetAllAsync()</c>; at
/// ~500-user scale the full set fits in memory trivially. Single writer:
/// only <c>ApplicationDecisionService</c> calls <see cref="Upsert"/> and
/// <see cref="Remove"/>, and only after its repository write has returned
/// successfully.
/// </remarks>
public interface IApplicationStore
{
    MemberApplication? GetById(Guid applicationId);

    /// <summary>
    /// Returns every application for the user, ordered by
    /// <c>SubmittedAt</c> descending (matches the old
    /// <c>GetUserApplicationsAsync</c> behavior).
    /// </summary>
    IReadOnlyList<MemberApplication> GetByUserId(Guid userId);

    /// <summary>
    /// Snapshot of all applications in the store.
    /// </summary>
    IReadOnlyList<MemberApplication> GetAll();

    /// <summary>
    /// Count of applications currently in the <see cref="ApplicationStatus.Submitted"/>
    /// state. Used by <see cref="IApplicationStore"/>-backed decorators to
    /// surface navigation badges without a DB round-trip.
    /// </summary>
    int CountSubmitted();

    /// <summary>
    /// Inserts or replaces the application entry keyed by <c>application.Id</c>.
    /// </summary>
    void Upsert(MemberApplication application);

    /// <summary>
    /// Removes an application entry by id. No-op if not present.
    /// </summary>
    void Remove(Guid applicationId);

    /// <summary>
    /// Replaces the entire contents of the store with
    /// <paramref name="applications"/>. Used by the startup warmup hosted
    /// service to populate the store once from the repository.
    /// </summary>
    void LoadAll(IReadOnlyList<MemberApplication> applications);
}
