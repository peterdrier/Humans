namespace Humans.Gdpr.Contracts;

/// <summary>
/// One contributor's slice of a user's GDPR export. A contributor returns the
/// personal data it owns for the target user, keyed by a section name that
/// appears in the export JSON. The export format is not a spec: it changes as
/// contributors change, and nothing outside this codebase reads it.
///
/// <para>
/// <b>Null semantics:</b> <see cref="Data"/> is <c>null</c> ONLY for
/// single-object sections whose underlying entity doesn't exist for this user
/// (for example, a profileless account has no <c>Profile</c>). Collection
/// sections MUST return an empty list (not <c>null</c>) when the user has no
/// records: a collection key is always present in the JSON, as <c>[]</c> when
/// empty. The orchestrator drops only <c>null</c> slices from the final
/// document.
/// </para>
/// </summary>
/// <param name="SectionName">
/// The section name this contributor chose for its slice (e.g.
/// <c>"Profile"</c>, <c>"ShiftSignups"</c>), declared as a constant on the
/// owning contributor. Must be unique across contributors — duplicates are a
/// bug — and must be a key of the same contributor's
/// <see cref="IUserDataContributor.ErasureDeclaration"/>; both are enforced at
/// runtime by the orchestrator.
/// </param>
/// <param name="Data">
/// Section-specific payload. Any shape that System.Text.Json can serialize
/// with the app's default options — typically an anonymous object (single-object
/// sections) or a list of anonymous objects (collection sections). An empty
/// list is a valid, non-dropped slice; <c>null</c> means the entire section is
/// missing and should be omitted from the export.
/// </param>
public sealed record UserDataSlice(string SectionName, object? Data);
