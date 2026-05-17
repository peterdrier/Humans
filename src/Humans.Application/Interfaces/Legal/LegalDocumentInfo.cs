using NodaTime;

namespace Humans.Application.Interfaces.Legal;

/// <summary>
/// Cached projection record for the Legal section's global cache (T-04).
/// Holds the active+required legal-document set in a shape suitable for
/// the every-page consent-banner read served by
/// <see cref="ILegalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync"/>.
/// One <c>LegalDocumentInfo</c> per active+required legal document.
/// </summary>
/// <remarks>
/// <para>
/// Footprint budget (CLAUDE.md "Scale and Deployment"): a handful of
/// documents (currently ~3–5; bounded by the number of Teams that scope
/// documents to themselves, which is at most a dozen at 500-user scale).
/// Each entry carries its versions inline including full multilingual
/// content; at typical document sizes the whole cache is well under
/// 1 MB. The Spec's 50 MB per-projection budget is not approached.
/// </para>
/// <para>
/// Built and held by <c>CachingLegalDocumentSyncService</c>. Invalidation
/// is wholesale (<c>Clear</c>) — there is no per-document key, because
/// the bag of "all active+required documents" is the unit consumed by
/// every read. Writes (admin create/update/archive/sync, version add) go
/// through <c>LegalDocumentSaveChangesInterceptor</c>, which clears the
/// cache after <c>SaveChangesAsync</c>.
/// </para>
/// </remarks>
/// <param name="Id">Document id.</param>
/// <param name="Name">Display name.</param>
/// <param name="TeamId">Owning Team id (cross-section FK).</param>
/// <param name="TeamName">Cached team display name, captured at warm time.</param>
/// <param name="LastSyncedAt">Last successful GitHub sync timestamp.</param>
/// <param name="Versions">Every version row for the document, ordered by EffectiveFrom ascending.</param>
public sealed record LegalDocumentInfo(
    Guid Id,
    string Name,
    Guid TeamId,
    string TeamName,
    Instant LastSyncedAt,
    IReadOnlyList<LegalDocumentVersionSnapshot> Versions);
