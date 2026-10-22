namespace Humans.Gdpr.Contracts;

/// <summary>The Article 15 download document.</summary>
/// <param name="ExportedAt">ISO-8601 UTC.</param>
/// <param name="UserId">The surviving account when the request named a merged-away id.</param>
/// <param name="MergedFromUserIds">
/// Archived ids merged into <paramref name="UserId"/>. Slices keep rows keyed to them (audit, consent, votes),
/// so without this list those rows look like someone else's data.
/// </param>
/// <param name="Sections">Keyed by <see cref="UserDataSlice.SectionName"/>, in contributor order.</param>
public sealed record GdprExport(
    string ExportedAt,
    Guid UserId,
    IReadOnlyList<Guid> MergedFromUserIds,
    IReadOnlyDictionary<string, object?> Sections);
