using System.Text;
using System.Text.RegularExpressions;
using Humans.Application.Services;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Wraps each "## As a …" block in a raw HTML div carrying role metadata so the
/// rendered HTML can be role-filtered per request. Blocks end at the next "## " heading
/// or EOF.
/// </summary>
public sealed class GuideMarkdownPreprocessor
{
    private static readonly Regex RoleHeading = new(
        @"^##\s+As\s+an?\s+(?:\[)?(?<head>Volunteer|Coordinator|Board)[^\n]*?(?:\((?<paren>[^)]+)\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex AnyH2 = new(
        @"^##\s+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public string Wrap(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Split('\n');
        var output = new StringBuilder(markdown.Length + 256);
        var inBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

            var roleMatch = RoleHeading.Match(line);
            if (roleMatch.Success)
            {
                if (inBlock)
                {
                    AppendDivClose(output);
                }

                var head = roleMatch.Groups["head"].Value.ToLowerInvariant() switch
                {
                    "volunteer" => "volunteer",
                    "coordinator" => "coordinator",
                    "board" => "boardadmin",
                    _ => "volunteer"
                };

                var parenContent = roleMatch.Groups["paren"].Success
                    ? roleMatch.Groups["paren"].Value
                    : null;
                var roles = GuideRolePrivilegeMap.ParseParenthetical(parenContent);
                var rolesAttr = string.Join(",", roles);

                output.Append('\n');
                output.Append($"<div data-guide-role=\"{head}\" data-guide-roles=\"{rolesAttr}\">");
                output.Append('\n');
                output.Append('\n');
                output.Append(rawLine);
                output.Append('\n');
                inBlock = true;
                continue;
            }

            if (inBlock && AnyH2.IsMatch(line))
            {
                AppendDivClose(output);
                inBlock = false;
            }

            output.Append(rawLine);
            if (i < lines.Length - 1)
            {
                output.Append('\n');
            }
        }

        if (inBlock)
        {
            AppendDivClose(output);
        }

        return output.ToString();
    }

    private static void AppendDivClose(StringBuilder sb)
    {
        sb.Append('\n');
        sb.Append("</div>");
        sb.Append('\n');
        sb.Append('\n');
    }
}
