using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Guide.Services;

namespace Humans.Guide.Tests.Services;

public class GuideMarkdownPreprocessorTests
{
    private static readonly GuideMarkdownPreprocessor Preprocessor = new();

    [HumansFact]
    public void Wrap_VolunteerBlock_WrapsWithDivVolunteerRole()
    {
        const string input = """
            # Profiles

            ## What this section is for

            Intro.

            ## As a Volunteer

            Do volunteer things.

            ## Related sections
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("<div data-guide-role=\"volunteer\" data-guide-roles=\"\">");
        result.Should().Contain("## As a Volunteer");
        result.Should().Contain("</div>");
    }

    [HumansFact]
    public void Wrap_CoordinatorWithParenthetical_CapturesRoles()
    {
        const string input = """
            ## As a Coordinator (Consent Coordinator)

            Do consent-coordinator things.

            ## Related sections
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("<div data-guide-role=\"coordinator\" data-guide-roles=\"ConsentCoordinator\">");
    }

    [HumansFact]
    public void Wrap_BoardAdminWithParenthetical_CapturesSystemRole()
    {
        const string input = """
            ## As a Board member / Admin (Camp Admin)

            Do camp admin things.
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("<div data-guide-role=\"boardadmin\" data-guide-roles=\"CampAdmin\">");
    }

    [HumansFact]
    public void Wrap_HeadingWithGlossaryLink_StillMatches()
    {
        const string input = """
            ## As a [Volunteer](Glossary.md#volunteer)

            Content.
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("<div data-guide-role=\"volunteer\" data-guide-roles=\"\">");
    }

    [HumansFact]
    public void Wrap_ClosesDivBeforeNextSectionHeading()
    {
        const string input = """
            ## As a Volunteer

            Volunteer stuff.

            ## As a Coordinator

            Coordinator stuff.
            """;

        var result = Preprocessor.Wrap(input);

        // A closing div must appear before the next As-a heading's opening div.
        var firstOpen = result.IndexOf("<div data-guide-role=\"volunteer\"", StringComparison.Ordinal);
        var firstClose = result.IndexOf("</div>", firstOpen, StringComparison.Ordinal);
        var secondOpen = result.IndexOf("<div data-guide-role=\"coordinator\"", StringComparison.Ordinal);

        firstOpen.Should().BeGreaterThan(-1);
        firstClose.Should().BeGreaterThan(firstOpen);
        secondOpen.Should().BeGreaterThan(firstClose);
    }

    [HumansFact]
    public void Wrap_ClosesDivAtRelatedSectionsHeading()
    {
        const string input = """
            ## As a Volunteer

            Content.

            ## Related sections

            See other stuff.
            """;

        var result = Preprocessor.Wrap(input);

        var open = result.IndexOf("<div data-guide-role=\"volunteer\"", StringComparison.Ordinal);
        var close = result.IndexOf("</div>", open, StringComparison.Ordinal);
        var related = result.IndexOf("## Related sections", StringComparison.Ordinal);

        open.Should().BeGreaterThan(-1);
        close.Should().BeGreaterThan(open);
        related.Should().BeGreaterThan(close);
    }

    [HumansFact]
    public void Wrap_NoAsAHeadings_ReturnsInputUnchanged()
    {
        const string input = """
            # Glossary

            ## Admin

            A human with full access.

            ## Board

            The governance body.
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Be(input);
    }

    [HumansFact]
    public void Wrap_BlankLineBeforeAndAfterDiv_SoMarkdigRendersInner()
    {
        const string input = """
            Intro paragraph.

            ## As a Volunteer

            Content.
            """;

        var result = Preprocessor.Wrap(input);

        // Markdig requires HTML block tags to be separated from inline markdown by blank lines.
        result.Should().Contain("\n\n<div data-guide-role=\"volunteer\"");
    }

    [HumansFact]
    public void Wrap_ParentheticalWithUnknownToken_OmitsFromDataAttributeButKeepsHeading()
    {
        const string input = """
            ## As a Board member / Admin (Camp Admin, Mystery Role)

            Content.
            """;

        var result = Preprocessor.Wrap(input);

        // Unknown tokens must not appear in the data-guide-roles filter attribute.
        result.Should().Contain("data-guide-roles=\"CampAdmin\"");
        result.Should().NotMatch("data-guide-roles=\"*Mystery*\"");

        // The heading itself is passed through unchanged so that any markdown
        // links inside it (e.g. "[Volunteer](Glossary.md#volunteer)") survive.
        result.Should().Contain("## As a Board member / Admin (Camp Admin, Mystery Role)");
    }

    [HumansFact]
    public void Wrap_HeadingWithGlossaryLinkAndRoleParenthetical_PreservesLink()
    {
        // Regression: the heading has a markdown link whose URL is parenthesised,
        // and a trailing role parenthetical. The preprocessor must not mangle
        // either — the link's URL must survive so Markdig can render it, and the
        // captured parenthetical must be the trailing role, never the link target.
        const string input = """
            ## As a [Coordinator](Glossary.md#coordinator) (Camp Lead)

            Content.
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("[Coordinator](Glossary.md#coordinator)");
        result.Should().Contain("(Camp Lead)");
        result.Should().Contain($"data-guide-roles=\"{GuideRolePrivilegeMap.CampLead}\"");
    }

    [HumansFact]
    public void Wrap_ShippedCampsLeadHeading_CarriesTheCampLeadToken()
    {
        // End-to-end on the real file: nobodies-collective/Humans#1035 was invisible
        // precisely because this heading rendered data-guide-roles="".
        var markdown = File.ReadAllText(
            Path.Combine(LocateRepoRoot(), "docs", "guide", "Camps.md"));

        var result = Preprocessor.Wrap(markdown);

        result.Should().Contain(
            $"<div data-guide-role=\"coordinator\" data-guide-roles=\"{GuideRolePrivilegeMap.CampLead}\">");
    }

    [HumansFact]
    public void Wrap_EveryRoleHeadingInShippedContent_LandsInsideARoleDiv()
    {
        var guideDir = Path.Combine(LocateRepoRoot(), "docs", "guide");
        var unwrapped = new List<string>();

        foreach (var file in Directory.GetFiles(guideDir, "*.md"))
        {
            var markdown = File.ReadAllText(file);
            var wrapped = Preprocessor.Wrap(markdown);

            foreach (var heading in RoleHeadings(markdown))
            {
                if (!IsInsideRoleDiv(wrapped, heading))
                {
                    unwrapped.Add($"{Path.GetFileName(file)}: {heading}");
                }
            }
        }

        unwrapped.Should().BeEmpty(
            "an 'As a …' heading the preprocessor leaves unwrapped is served to every visitor, "
            + "anonymous included — GuideFilter can only strip blocks that carry a data-guide-role div");
    }

    private static readonly Regex AnyRoleHeading = new(
        @"^##\s+As\s+an?\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static IEnumerable<string> RoleHeadings(string markdown) =>
        markdown.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => AnyRoleHeading.IsMatch(l));

    private static bool IsInsideRoleDiv(string wrapped, string heading)
    {
        var at = wrapped.IndexOf(heading, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var before = wrapped[..at];
        return CountOccurrences(before, "<div data-guide-role=") > CountOccurrences(before, "</div>");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repository root (no Humans.slnx above " + AppContext.BaseDirectory + ").");
    }
}
