using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Guide.Services;

namespace Humans.Guide.Tests.Services;

public class GuideSegmenterTests
{
    private static GuideSegment Scoped(string markdown, string role) =>
        GuideSegmenter.Segment(markdown).Segments.Single(s => string.Equals(s.Role, role, StringComparison.Ordinal));

    [HumansFact]
    public void Segment_VolunteerBlock_CarriesTheVolunteerRole()
    {
        const string input = """
            # Profiles

            ## What this section is for

            Intro.

            ## As a Volunteer

            Do volunteer things.

            ## Related sections
            """;

        var segment = Scoped(input, GuideDocument.Volunteer);

        segment.Privileges.Should().BeEmpty();
        segment.Markdown.Should().Contain("## As a Volunteer");
        segment.Markdown.Should().Contain("Do volunteer things.");
        segment.Markdown.Should().NotContain("Related sections");
    }

    [HumansFact]
    public void Segment_CoordinatorWithParenthetical_CapturesRoles()
    {
        const string input = """
            ## As a Coordinator (Consent Coordinator)

            Do consent-coordinator things.

            ## Related sections
            """;

        Scoped(input, GuideDocument.Coordinator)
            .Privileges.Should().ContainSingle().Which.Should().Be("ConsentCoordinator");
    }

    [HumansFact]
    public void Segment_BoardAdminWithParenthetical_CapturesSystemRole()
    {
        const string input = """
            ## As a Board member / Admin (Camp Admin)

            Do camp admin things.
            """;

        Scoped(input, GuideDocument.BoardAdmin)
            .Privileges.Should().ContainSingle().Which.Should().Be("CampAdmin");
    }

    [HumansFact]
    public void Segment_HeadingWithGlossaryLink_StillMatches()
    {
        const string input = """
            ## As a [Volunteer](Glossary.md#volunteer)

            Content.
            """;

        Scoped(input, GuideDocument.Volunteer).Privileges.Should().BeEmpty();
    }

    [HumansFact]
    public void Segment_EndsBlockAtNextRoleHeading()
    {
        const string input = """
            ## As a Volunteer

            Volunteer stuff.

            ## As a Coordinator

            Coordinator stuff.
            """;

        var document = GuideSegmenter.Segment(input);

        document.Segments.Should().HaveCount(2);
        document.Segments[0].Role.Should().Be(GuideDocument.Volunteer);
        document.Segments[0].Markdown.Should().NotContain("Coordinator stuff.");
        document.Segments[1].Role.Should().Be(GuideDocument.Coordinator);
    }

    [HumansFact]
    public void Segment_EndsBlockAtRelatedSectionsHeading()
    {
        const string input = """
            ## As a Volunteer

            Content.

            ## Related sections

            See other stuff.
            """;

        var document = GuideSegmenter.Segment(input);

        document.Segments.Should().HaveCount(2);
        document.Segments[0].Role.Should().Be(GuideDocument.Volunteer);
        document.Segments[1].Role.Should().BeNull(because: "a plain ## heading closes the block");
        document.Segments[1].Markdown.Should().Contain("See other stuff.");
    }

    [HumansFact]
    public void Segment_NoAsAHeadings_IsOneUnscopedSegment()
    {
        const string input = """
            # Glossary

            ## Admin

            A human with full access.

            ## Board

            The governance body.
            """;

        var document = GuideSegmenter.Segment(input);

        document.Segments.Should().ContainSingle();
        document.Segments[0].Role.Should().BeNull();
        document.Segments[0].Markdown.Should().Be(input);
    }

    [HumansFact]
    public void Segment_ParentheticalWithUnknownToken_OmitsItButKeepsHeading()
    {
        const string input = """
            ## As a Board member / Admin (Camp Admin, Mystery Role)

            Content.
            """;

        var segment = Scoped(input, GuideDocument.BoardAdmin);

        // Unknown tokens must not become a privilege nobody can hold.
        segment.Privileges.Should().ContainSingle().Which.Should().Be("CampAdmin");

        // The heading itself is passed through unchanged so that any markdown
        // links inside it (e.g. "[Volunteer](Glossary.md#volunteer)") survive.
        segment.Markdown.Should().Contain("## As a Board member / Admin (Camp Admin, Mystery Role)");
    }

    [HumansFact]
    public void Segment_HeadingWithGlossaryLinkAndRoleParenthetical_PreservesLink()
    {
        // Regression: the heading has a markdown link whose URL is parenthesised,
        // and a trailing role parenthetical. The segmenter must not mangle either — the
        // link's URL must survive so Markdig can render it, and the captured
        // parenthetical must be the trailing role, never the link target.
        const string input = """
            ## As a [Coordinator](Glossary.md#coordinator) (Camp Lead)

            Content.
            """;

        var segment = Scoped(input, GuideDocument.Coordinator);

        segment.Markdown.Should().Contain("[Coordinator](Glossary.md#coordinator)");
        segment.Markdown.Should().Contain("(Camp Lead)");
        segment.Privileges.Should().Contain(GuideRolePrivilegeMap.CampLead);
    }

    [HumansFact]
    public void Segment_ShippedCampsLeadHeading_CarriesTheCampLeadToken()
    {
        // End-to-end on the real file: nobodies-collective/Humans#1035 was invisible
        // precisely because this heading resolved to no privilege at all.
        var markdown = File.ReadAllText(
            Path.Combine(LocateRepoRoot(), "docs", "guide", "Camps.md"));

        GuideSegmenter.Segment(markdown).Segments
            .Where(s => string.Equals(s.Role, GuideDocument.Coordinator, StringComparison.Ordinal))
            .SelectMany(s => s.Privileges)
            .Should().Contain(GuideRolePrivilegeMap.CampLead);
    }

    [HumansFact]
    public void Segment_EveryRoleHeadingInShippedContent_OpensAScopedSegment()
    {
        var guideDir = Path.Combine(LocateRepoRoot(), "docs", "guide");
        var unscoped = new List<string>();

        foreach (var file in Directory.GetFiles(guideDir, "*.md"))
        {
            var document = GuideSegmenter.Segment(File.ReadAllText(file));

            foreach (var heading in RoleHeadings(File.ReadAllText(file)))
            {
                var owner = document.Segments.FirstOrDefault(s => s.Markdown.Contains(heading, StringComparison.Ordinal));
                if (owner is null || owner.Role is null)
                {
                    unscoped.Add($"{Path.GetFileName(file)}: {heading}");
                }
            }
        }

        unscoped.Should().BeEmpty(
            "an 'As a …' heading the segmenter leaves unscoped is served to every visitor, "
            + "anonymous included — GuideFilter can only drop segments that carry a role");
    }

    [HumansFact]
    public void Segment_EveryShippedFile_RejoinsToTheOriginal()
    {
        // The filter selects segments and joins them; if the join is not lossless, a reader who
        // can see everything gets a mangled page. Proven on the whole shipped corpus.
        foreach (var file in Directory.GetFiles(Path.Combine(LocateRepoRoot(), "docs", "guide"), "*.md"))
        {
            var markdown = File.ReadAllText(file);
            var rejoined = string.Join('\n',
                GuideSegmenter.Segment(markdown).Segments.Select(s => s.Markdown));

            rejoined.Should().Be(markdown, because: $"{Path.GetFileName(file)} must survive a round trip");
        }
    }

    private static readonly Regex AnyRoleHeading = new(
        @"^##\s+As\s+an?\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static IEnumerable<string> RoleHeadings(string markdown) =>
        markdown.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => AnyRoleHeading.IsMatch(l));

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
