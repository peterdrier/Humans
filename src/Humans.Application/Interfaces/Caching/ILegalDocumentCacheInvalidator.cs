namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-section signal for the global Legal-document cache (T-04). Implemented
/// by the Singleton <c>CachingLegalDocumentSyncService</c> decorator and
/// consumed by the <c>LegalDocumentSaveChangesInterceptor</c>, which fires
/// after EF persists any write to <c>legal_documents</c> or
/// <c>document_versions</c>.
/// </summary>
/// <remarks>
/// The Legal cache is bag-shaped (whole-set replacement on rebuild), so the
/// only operation is wholesale clear. There is no per-document key — the
/// cached unit is the full set of active+required documents that the
/// every-page consent-banner read consumes.
/// </remarks>
public interface ILegalDocumentCacheInvalidator
{
    /// <summary>
    /// Evict the entire Legal-document cache. Next read repopulates lazily.
    /// </summary>
    void InvalidateAll();
}
