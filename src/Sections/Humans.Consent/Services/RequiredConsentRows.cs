using Humans.Consent.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// Shapes the onboarding-widget Consents rows from the active+required document set and
/// the user's consented-version ids: one row per document at its current version (latest
/// <c>EffectiveFrom &lt;= now</c>), unsigned first so outstanding work bubbles to the top
/// of the widget.
/// </summary>
/// <remarks>
/// Single source of truth for both the repo-backed <see cref="ConsentService"/> read and
/// the <see cref="CachingConsentService"/> decorator, which compose the two inputs
/// differently (repo vs cache) but must produce the identical row shape and ordering.
/// Internal, and not a static on <see cref="RequiredConsentRow"/> itself, because it names
/// <see cref="ActiveRequiredLegalDocumentSnapshot"/> — a section type the contracts leaf
/// must not publish to carry a helper.
/// </remarks>
internal static class RequiredConsentRows
{
    public static IReadOnlyList<RequiredConsentRow> BuildOrdered(
        IReadOnlyList<ActiveRequiredLegalDocumentSnapshot> documents,
        IReadOnlySet<Guid> consentedVersionIds,
        Instant now)
    {
        var rows = new List<RequiredConsentRow>(documents.Count);
        foreach (var doc in documents)
        {
            var currentVersion = doc.Versions
                .Where(v => v.EffectiveFrom <= now)
                .MaxBy(v => v.EffectiveFrom);

            if (currentVersion is null)
                continue;

            rows.Add(new RequiredConsentRow(
                DocumentVersionId: currentVersion.Id,
                Title: doc.Name,
                Signed: consentedVersionIds.Contains(currentVersion.Id)));
        }

        return rows
            .OrderBy(r => r.Signed)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .ToList();
    }
}
