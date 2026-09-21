namespace Humans.Feedback;

/// <summary>
/// Marker type for Feedback's resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named
/// <c>.cs</c> file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Feedback</c> — <c>Humans.Feedback.Resources</c> would make every
/// Feedback string fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers
/// via <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set also carries the <c>Feedback_Email_*</c> keys: Feedback renders its own
/// response email through <c>FeedbackEmails</c>, so the copy lives with its owner
/// (peterdrier/Humans#1651).
/// </remarks>
public class FeedbackResource;
