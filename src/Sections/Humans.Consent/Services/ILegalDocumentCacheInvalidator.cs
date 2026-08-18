using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Consent.Services;

/// <summary>
/// Cross-section signal for the global Legal-document cache (T-04). Implemented
/// by the Singleton <c>CachingLegalDocumentSyncService</c> decorator and
/// consumed directly by <c>LegalDocumentSyncService</c> — the sole writer for
/// <c>legal_documents</c> and <c>document_versions</c> — after each successful
/// repository write (nobodies-collective/Humans#751).
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
internal interface ILegalDocumentCacheInvalidator : IInvalidator
{
    /// <summary>
    /// Evict the entire Legal-document cache. Next read repopulates lazily.
    /// </summary>
    void InvalidateAll();
}
