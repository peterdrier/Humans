namespace Humans.Issues;

/// <summary>
/// Marker type for Issues' resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named
/// <c>.cs</c> file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Issues</c> — <c>Humans.Issues.Resources</c> would make every
/// Issues string fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers
/// via <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set also covers the floating help widget's issue-submission modal, which lives here
/// as <c>Views/Shared/_IssueWidgetModal.cshtml</c> so the whole <c>Issue_*</c> set stays in
/// one resource set.
/// </remarks>
public class IssuesResource;
