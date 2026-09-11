using System.Text.RegularExpressions;

namespace Humans.Guide.Services;

/// <summary>
/// Splits a guide file into role-scoped segments. A "## As a …" heading opens a segment that
/// runs to the next <c>##</c> heading or EOF; everything else is an unscoped segment visible to
/// everyone.
/// </summary>
/// <remarks>
/// Static, like <see cref="GuideFilter"/> and <see cref="GuideRolePrivilegeMap"/>: no state, no
/// dependency, nothing that wants a seam.
/// </remarks>
internal static class GuideSegmenter
{
    // Internal so the guard test can hold guide content to the exact heading grammar the
    // segmenter uses, rather than a second copy that can drift from it.
    internal static readonly Regex RoleHeading = new(
        @"^##\s+As\s+an?\s+(?:\[)?(?<head>Volunteer|Coordinator|Board)[^\n]*?(?:\((?<paren>[^)]+)\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex AnyH2 = new(
        @"^##\s+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    // A "## …" line inside a fenced code block is sample text, not a heading. Untracked, it ends
    // the role block it sits in and serves the rest of that block to everyone — so fence state is
    // an authorization concern here, not a rendering nicety.
    private static readonly Regex FenceDelimiter = new(
        @"^ {0,3}(?<fence>`{3,}|~{3,})",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly string[] NoPrivileges = [];

    public static GuideDocument Segment(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Split('\n');
        var segments = new List<GuideSegment>();

        var start = 0;
        string? role = null;
        string? openFence = null;
        IReadOnlyList<string> privileges = NoPrivileges;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];

            var fence = FenceDelimiter.Match(line);
            if (fence.Success)
            {
                var delimiter = fence.Groups["fence"].Value;
                if (openFence is null)
                {
                    openFence = delimiter;
                }
                else if (delimiter[0] == openFence[0]
                    && delimiter.Length >= openFence.Length
                    && line.AsSpan(fence.Length).IsWhiteSpace())
                {
                    // A closing fence carries no info string, so anything trailing means this is
                    // still content — closing early would let the next "##" split the block.
                    openFence = null;
                }

                continue;
            }

            if (openFence is not null)
            {
                continue;
            }

            var roleMatch = RoleHeading.Match(line);
            if (roleMatch.Success)
            {
                Flush(i);
                role = roleMatch.Groups["head"].Value.ToLowerInvariant() switch
                {
                    "coordinator" => GuideDocument.Coordinator,
                    "board" => GuideDocument.BoardAdmin,
                    _ => GuideDocument.Volunteer
                };
                privileges = GuideRolePrivilegeMap.ParseParenthetical(
                    roleMatch.Groups["paren"].Success ? roleMatch.Groups["paren"].Value : null);
                start = i;
                continue;
            }

            if (role is not null && AnyH2.IsMatch(line))
            {
                Flush(i);
                role = null;
                privileges = NoPrivileges;
                start = i;
            }
        }

        Flush(lines.Length);

        return new GuideDocument(segments);

        // Closes the open segment at [start, end). Never emits the empty segment that a role
        // heading on line 0 would otherwise produce.
        void Flush(int end)
        {
            if (end <= start)
            {
                return;
            }

            segments.Add(new GuideSegment(
                role,
                privileges,
                string.Join('\n', lines[start..end])));
        }
    }
}
