namespace Humans.Gdpr.Contracts;

/// <summary>
/// Envelope returned by <see cref="IGdprService"/> — a timestamped bag of
/// section slices keyed by <see cref="UserDataSlice.SectionName"/>. This is the
/// shape serialized to the JSON file the user downloads.
/// </summary>
/// <param name="ExportedAt">
/// Invariant ISO-8601 instant string (UTC) when the export was generated.
/// Uses <c>Humans.Base.Extensions.DateFormattingExtensions.ToIso8601</c>.
/// </param>
/// <param name="UserId">
/// The account the export belongs to. When the request came in under an id that has
/// since been merged away, this is the surviving account's id, not the one asked with.
/// </param>
/// <param name="MergedFromUserIds">
/// The archived ids merged into <paramref name="UserId"/>, oldest-sorted; empty when the
/// account has never absorbed another. Section slices deliberately keep rows keyed to the
/// archived id — audit entries, consent records, assembly-vote rosters and ballots — so
/// without this list a row reading <c>UserId: 3</c> inside an export for account 5 looks
/// like someone else's data. It is the key that makes those rows legible.
/// </param>
/// <param name="Sections">
/// Section name → section data, in the order the contributors were called. Keys
/// are the stable JSON property names from <see cref="GdprExportSections"/>.
/// </param>
public sealed record GdprExport(
    string ExportedAt,
    Guid UserId,
    IReadOnlyList<Guid> MergedFromUserIds,
    IReadOnlyDictionary<string, object?> Sections);
