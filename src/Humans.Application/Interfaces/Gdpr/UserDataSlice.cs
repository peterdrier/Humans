namespace Humans.Application.Interfaces.Gdpr;

/// <summary>
/// One contributor's slice of a user's GDPR export. A contributor returns the
/// personal data it owns for the target user, keyed by a stable section name
/// that appears in the export JSON. <see cref="Data"/> may be <c>null</c> when
/// the contributor has nothing to report for this user — the orchestrator drops
/// null slices from the final document.
/// </summary>
/// <param name="SectionName">
/// Stable JSON property name (e.g. <c>"Profile"</c>, <c>"ShiftSignups"</c>).
/// Section names must be unique across contributors — duplicates are a bug.
/// </param>
/// <param name="Data">
/// Section-specific payload. Any shape that System.Text.Json can serialize with
/// the app's default options — typically an anonymous object or a list of them.
/// Null means "no data for this user in this section".
/// </param>
public sealed record UserDataSlice(string SectionName, object? Data);
