namespace Humans.Application.Interfaces.Gdpr;

/// <summary>
/// One contributor's slice of a user's GDPR export. A contributor returns the
/// personal data it owns for the target user, keyed by a stable section name
/// that appears in the export JSON.
///
/// <para>
/// <b>Null semantics:</b> <see cref="Data"/> is <c>null</c> ONLY for
/// single-object sections whose underlying entity doesn't exist for this user
/// (for example, a profileless account has no <c>Profile</c>). Collection
/// sections MUST return an empty list (not <c>null</c>) when the user has no
/// records — the legacy <c>ExportDataAsync</c> JSON shape always emitted
/// collection top-level keys as <c>[]</c>, and downstream consumers
/// (comparison tools, imports, support procedures) rely on that stability.
/// The orchestrator drops only <c>null</c> slices from the final document.
/// </para>
/// </summary>
/// <param name="SectionName">
/// Stable JSON property name (e.g. <c>"Profile"</c>, <c>"ShiftSignups"</c>).
/// Section names must be unique across contributors — duplicates are a bug.
/// </param>
/// <param name="Data">
/// Section-specific payload. Any shape that System.Text.Json can serialize
/// with the app's default options — typically an anonymous object (single-object
/// sections) or a list of anonymous objects (collection sections). An empty
/// list is a valid, non-dropped slice; <c>null</c> means the entire section is
/// missing and should be omitted from the export.
/// </param>
public sealed record UserDataSlice(string SectionName, object? Data);
