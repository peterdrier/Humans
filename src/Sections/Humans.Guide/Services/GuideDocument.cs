namespace Humans.Guide.Services;

/// <summary>
/// One run of consecutive lines from a guide file, carrying the role scope that decides who
/// sees it. <paramref name="Role"/> is null for text outside any "## As a …" block — the
/// prologue, and everything from the next non-"As a" <c>##</c> heading onward — which is
/// visible to everyone.
/// </summary>
/// <param name="Privileges">
/// The privilege tokens the heading's parenthetical resolved to, via
/// <see cref="GuideRolePrivilegeMap"/>. Empty for an unscoped heading.
/// </param>
internal sealed record GuideSegment(
    string? Role,
    IReadOnlyList<string> Privileges,
    string Markdown);

/// <summary>
/// A guide file split into role-scoped segments, in source order. Joining every segment's
/// markdown with newlines reproduces the original file exactly — the filter works by dropping
/// segments from that join, never by rewriting their contents.
/// </summary>
internal sealed record GuideDocument(IReadOnlyList<GuideSegment> Segments)
{
    /// <summary>The roles the wrapper recognises, as they appear on <see cref="GuideSegment"/>.</summary>
    public const string Volunteer = "volunteer";
    public const string Coordinator = "coordinator";
    public const string BoardAdmin = "boardadmin";
}
