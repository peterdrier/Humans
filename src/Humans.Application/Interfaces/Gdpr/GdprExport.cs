namespace Humans.Application.Interfaces.Gdpr;

/// <summary>
/// Envelope returned by <see cref="IGdprExportService"/> — a timestamped bag of
/// section slices keyed by <see cref="UserDataSlice.SectionName"/>. This is the
/// shape serialized to the JSON file the user downloads.
/// </summary>
/// <param name="ExportedAt">
/// Invariant ISO-8601 instant string (UTC) when the export was generated.
/// Uses <c>Humans.Application.Extensions.NodaTimeFormattingExtensions.ToInvariantInstantString</c>.
/// </param>
/// <param name="Sections">
/// Ordered dictionary of section name → section data. Keys are stable JSON
/// property names; values match the legacy profile-export shape section-for-section.
/// </param>
public sealed record GdprExport(
    string ExportedAt,
    IReadOnlyDictionary<string, object?> Sections);
