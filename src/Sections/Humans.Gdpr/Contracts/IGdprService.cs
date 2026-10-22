using Humans.Base.Interfaces;

namespace Humans.Gdpr.Contracts;

public interface IGdprService : IOrchestrator
{
    /// <summary>
    /// Contributors receive the surviving account id; the envelope also lists the merged-away ids.
    /// Fails if any contributor fails.
    /// </summary>
    Task<GdprExport> ExportForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Runs every contributor's erasure for this one id, the <see cref="IUserDataContributor.ErasesLast"/>
    /// one last. A contributor that throws aborts the run. The caller loops the merge chain.
    /// </summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct = default);
}
