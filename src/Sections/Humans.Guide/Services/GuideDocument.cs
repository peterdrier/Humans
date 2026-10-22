namespace Humans.Guide.Services;

/// <summary>
/// <paramref name="Role"/> is null for text outside any "## As a …" block, which everyone sees.
/// <paramref name="Privileges"/> are the heading parenthetical's tokens via <see cref="GuideRolePrivilegeMap"/>, empty
/// for an unscoped heading.
/// </summary>
internal sealed record GuideSegment(
    string? Role,
    IReadOnlyList<string> Privileges,
    string Markdown);

/// <summary>
/// Segments in source order. Joining their markdown with newlines reproduces the file exactly: the filter only drops
/// segments, never rewrites them.
/// </summary>
internal sealed record GuideDocument(IReadOnlyList<GuideSegment> Segments)
{
    /// <summary>The roles the wrapper recognises, as they appear on <see cref="GuideSegment"/>.</summary>
    public const string Volunteer = "volunteer";
    public const string Coordinator = "coordinator";
    public const string BoardAdmin = "boardadmin";
}
