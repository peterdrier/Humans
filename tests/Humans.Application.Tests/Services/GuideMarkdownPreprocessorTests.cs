using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GuideMarkdownPreprocessorTests
{
    private static readonly GuideMarkdownPreprocessor Preprocessor = new();

    [Fact]
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

    [Fact]
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

    [Fact]
    public void Wrap_BoardAdminWithParenthetical_CapturesSystemRole()
    {
        const string input = """
            ## As a Board member / Admin (Camp Admin)

            Do camp admin things.
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("<div data-guide-role=\"boardadmin\" data-guide-roles=\"CampAdmin\">");
    }

    [Fact]
    public void Wrap_HeadingWithGlossaryLink_StillMatches()
    {
        const string input = """
            ## As a [Volunteer](Glossary.md#volunteer)

            Content.
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("<div data-guide-role=\"volunteer\" data-guide-roles=\"\">");
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public void Wrap_ParentheticalWithUnknownToken_OmitsUnknown()
    {
        const string input = """
            ## As a Board member / Admin (Camp Admin, Mystery Role)

            Content.
            """;

        var result = Preprocessor.Wrap(input);

        result.Should().Contain("data-guide-roles=\"CampAdmin\"");
        result.Should().NotContain("Mystery");
    }
}
