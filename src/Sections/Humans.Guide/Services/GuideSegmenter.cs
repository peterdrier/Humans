using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace Humans.Guide.Services;

/// <summary>
/// A "## As a …" heading opens a segment that runs to the next <c>##</c> heading or EOF; everything else is an
/// unscoped segment everyone sees.
/// </summary>
internal sealed class GuideSegmenter(GuideRolePrivilegeMap privileges)
{
    // Internal so the guard test can hold guide content to the exact heading grammar the
    // segmenter uses, rather than a second copy that can drift from it.
    internal static readonly Regex RoleHeading = new(
        @"^ {0,3}##[ \t]+As\s+an?\s+(?:\[)?(?<head>Volunteer|Coordinator|Board)[^\n]*?(?:\((?<paren>[^)]+)\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex AnyH2 = new(
        @"^ {0,3}##(?:[ \t]+|$)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly string[] NoPrivileges = [];

    public GuideDocument Segment(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Split('\n');
        var segments = new List<GuideSegment>();
        // Use the renderer's block grammar: sample headings inside code/HTML cannot end a restricted block.
        var headingLines = Markdown.Parse(markdown, GuideRenderer.Pipeline)
            .Descendants<HeadingBlock>()
            .Where(heading => heading.Level == 2)
            .Select(heading => heading.Line)
            .ToHashSet();

        var start = 0;
        string? role = null;
        IReadOnlyList<string> privilegeTokens = NoPrivileges;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!headingLines.Contains(i)) continue;
            var line = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];

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
                privilegeTokens = privileges.ParseParenthetical(
                    roleMatch.Groups["paren"].Success ? roleMatch.Groups["paren"].Value : null);
                start = i;
                continue;
            }

            if (role is not null && AnyH2.IsMatch(line))
            {
                Flush(i);
                role = null;
                privilegeTokens = NoPrivileges;
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
                privilegeTokens,
                string.Join('\n', lines[start..end])));
        }
    }
}
