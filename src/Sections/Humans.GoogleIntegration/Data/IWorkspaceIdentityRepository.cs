using Humans.Base.Interfaces.Repositories;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Domain;
using NodaTime;

namespace Humans.GoogleIntegration.Data;

/// <summary>Sole reader and writer of <c>google_workspace_identities</c>.</summary>
internal interface IWorkspaceIdentityRepository : IRepository
{
    Task<WorkspaceIdentityRecord?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkspaceIdentityRecord>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or overwrites the member's row.</summary>
    Task UpsertAsync(Guid userId, string? email, GoogleEmailStatus status, Instant now, CancellationToken ct = default);

    /// <summary>Inserts the row only when the member has none; false when one already exists.</summary>
    Task<bool> TryInsertAsync(Guid userId, string? email, GoogleEmailStatus status, Instant now, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default);
}
