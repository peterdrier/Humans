using Humans.Consent.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// Unsigned first. The one shape for both <see cref="ConsentService"/> and <see cref="CachingConsentService"/>,
/// which compose the inputs differently but must produce identical rows.
/// </summary>
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
