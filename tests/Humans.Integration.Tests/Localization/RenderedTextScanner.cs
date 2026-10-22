using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Humans.Integration.Tests.Localization;

/// <summary>
/// Extracts visible text runs (text nodes plus placeholder/title/alt/aria-label) from rendered HTML and tests them for
/// the pseudo-localizer marker. Script and style are skipped, and letterless runs are dropped.
/// </summary>
internal static class RenderedTextScanner
{
    private static readonly HtmlParser Parser = new();

    private static readonly HashSet<string> SkipContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "HEAD", "SVG",
    };

    private static readonly string[] VisibleTextAttributes = ["placeholder", "title", "alt", "aria-label"];

    /// <summary>
    /// One run per visible text node, scoped to <c>&lt;main&gt;</c> (or the body, without one) so the layout shell,
    /// identical on every page, doesn't drown the per-page signal.
    /// </summary>
    public static IReadOnlyList<string> ExtractTextRuns(string html)
    {
        var document = Parser.ParseDocument(html);
        var root = document.QuerySelector("main") ?? document.Body;
        if (root is null)
            return [];

        var runs = new List<string>();
        var stack = new Stack<INode>();
        PushChildren(stack, root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case IText text:
                    AddRun(runs, text.Data);
                    break;

                case IElement element when SkipContainers.Contains(element.TagName):
                    break; // don't descend into script/style/etc.

                case IElement element:
                    foreach (var attribute in VisibleTextAttributes)
                        AddRun(runs, element.GetAttribute(attribute));
                    PushChildren(stack, element);
                    break;

                default:
                    PushChildren(stack, node);
                    break;
            }
        }

        return runs;
    }

    private static void AddRun(List<string> runs, string? value)
    {
        if (value is null)
            return;
        var run = CollapseWhitespace(value);
        if (run.Length > 0 && ContainsLetter(run))
            runs.Add(run);
    }

    private static void PushChildren(Stack<INode> stack, INode node)
    {
        foreach (var child in node.ChildNodes)
            stack.Push(child);
    }

    public static bool HasMarker(string run) =>
        run.Contains(PseudoStringLocalizer.Open) || run.Contains(PseudoStringLocalizer.Close);

    private static bool ContainsLetter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetter(c))
                return true;
        }

        return false;
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
