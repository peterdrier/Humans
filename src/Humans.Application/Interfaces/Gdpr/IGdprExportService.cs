namespace Humans.Application.Interfaces.Gdpr;

/// <summary>
/// Orchestrates GDPR Article 15 data exports. Owns no tables — fans out to every
/// registered <see cref="IUserDataContributor"/> and merges the returned slices
/// into a single document for the user to download.
/// </summary>
public interface IGdprExportService
{
    /// <summary>
    /// Builds a complete GDPR export document for <paramref name="userId"/> by
    /// calling every registered contributor and merging their slices by section
    /// name. The resulting <see cref="GdprExport"/> serializes to the same JSON
    /// shape the legacy <c>ProfileService.ExportDataAsync</c> produced.
    /// </summary>
    Task<GdprExport> ExportForUserAsync(Guid userId, CancellationToken ct = default);
}
