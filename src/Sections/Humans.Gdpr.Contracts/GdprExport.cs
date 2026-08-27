namespace Humans.Gdpr.Contracts;

/// <summary>
/// Envelope returned by <see cref="IGdprService"/> — a timestamped bag of
/// section slices keyed by <see cref="UserDataSlice.SectionName"/>. This is the
/// shape serialized to the JSON file the user downloads.
/// </summary>
/// <param name="ExportedAt">
/// Invariant ISO-8601 instant string (UTC) when the export was generated.
/// Uses <c>Humans.Base.Extensions.NodaTimeFormattingExtensions.ToIso8601</c>.
/// </param>
/// <param name="Sections">
/// Section name → section data, in the order the contributors were called. Keys
/// are the stable JSON property names from <see cref="GdprExportSections"/>.
/// </param>
public sealed record GdprExport(
    string ExportedAt,
    IReadOnlyDictionary<string, object?> Sections);
