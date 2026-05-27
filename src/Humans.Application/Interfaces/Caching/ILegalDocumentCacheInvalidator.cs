using Humans.Application.Architecture;

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
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing legal-document cache flushed cross-section; remains until LegalDocumentService's caching decorator owns invalidation end-to-end.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface ILegalDocumentCacheInvalidator : IInvalidator
{
    /// <summary>
    /// Evict the entire Legal-document cache. Next read repopulates lazily.
    /// </summary>
    void InvalidateAll();
}
