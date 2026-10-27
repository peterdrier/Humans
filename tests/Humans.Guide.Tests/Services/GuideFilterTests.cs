using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Guide.Services;

namespace Humans.Guide.Tests.Services;

public class GuideFilterTests
{
    // The filter runs before Markdig, so these exercise segmentation and role selection together.
    private const string Sample = """
        Intro, always visible.

        ## As a Volunteer

        Volunteer content.

        ## As a Coordinator (Consent Coordinator)

        Coord content.

        ## As a Board member / Admin (Teams Admin)

        Teams admin content.

        ## Related sections

        Always visible.
        """;

    private const string CampsLike = """
        ## As a Coordinator (Camp Lead)

        Camp lead content.

        ## As a Board member / Admin (Camps Admin)

        Camp admin content.
        """;

    private static string Apply(string markdown, GuideRoleContext context) =>
        GuideFilter.Apply(GuideTestRoles.Segmenter.Segment(markdown), context);

    private static GuideRoleContext Roles(bool isCoord, params string[] systemRoles) =>
        new(IsAuthenticated: true, IsTeamCoordinator: isCoord, IsCampLead: false,
            SystemRoles: new HashSet<string>(systemRoles, StringComparer.Ordinal));

    private static GuideRoleContext CampLeadOnly() =>
        new(IsAuthenticated: true, IsTeamCoordinator: false, IsCampLead: true,
            SystemRoles: new HashSet<string>(StringComparer.Ordinal));

    [HumansFact]
    public void Apply_Anonymous_KeepsOnlyVolunteerBlock()
    {
        var result = Apply(Sample, GuideRoleContext.Anonymous);

        result.Should().Contain("Volunteer content.");
        result.Should().Contain("Intro, always visible.");
        result.Should().Contain("Related sections");
        result.Should().NotContain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_Anonymous_DropsTheHeadingAndNotJustTheBody()
    {
        // Filtering markdown means an invisible block, heading included, leaves nothing behind.
        var result = Apply(Sample, GuideRoleContext.Anonymous);

        result.Should().NotContain("As a Coordinator");
        result.Should().NotContain("As a Board member");
    }

    [HumansFact]
    public void Apply_PlainVolunteer_SameAsAnonymous()
    {
        var result = Apply(Sample, Roles(isCoord: false));

        result.Should().Contain("Volunteer content.");
        result.Should().NotContain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_TeamCoordinator_SeesVolunteerAndCoordinator()
    {
        var result = Apply(Sample, Roles(isCoord: true));

        result.Should().Contain("Volunteer content.");
        result.Should().Contain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_ConsentCoordinatorRoleOnly_SeesCoordinatorBlockByParenthetical()
    {
        var result = Apply(Sample, Roles(isCoord: false, GuideTestRoles.ConsentCoordinator));

        result.Should().Contain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_ConsentCoordinatorOnBareCoordinatorHeading_NotVisible()
    {
        const string bareCoord = """
            ## As a Coordinator

            Bare coord content.
            """;

        var result = Apply(bareCoord, Roles(isCoord: false, GuideTestRoles.ConsentCoordinator));

        result.Should().NotContain("Bare coord content.");
    }

    [HumansFact]
    public void Apply_TeamsAdmin_SeesCoordinatorAndBoardOnTeamsFile()
    {
        // Within-file superset: seeing Board/Admin via (Teams Admin) implies seeing Coordinator too.
        var result = Apply(Sample, Roles(isCoord: false, GuideTestRoles.TeamsAdmin));

        result.Should().Contain("Coord content.");
        result.Should().Contain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_TeamsAdminOnTicketsFile_SeesNothingBeyondVolunteer()
    {
        const string ticketsLike = """
            ## As a Volunteer

            V-body

            ## As a Coordinator

            C-body

            ## As a Board member / Admin (Ticket Admin)

            BA-body
            """;

        var result = Apply(ticketsLike, Roles(isCoord: false, GuideTestRoles.TeamsAdmin));

        result.Should().Contain("V-body");
        result.Should().NotContain("C-body");
        result.Should().NotContain("BA-body");
    }

    [HumansFact]
    public void Apply_Admin_SeesEverything()
    {
        var result = Apply(Sample, Roles(isCoord: false, RoleNames.Admin));

        result.Should().Contain("Volunteer content.");
        result.Should().Contain("Coord content.");
        result.Should().Contain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_Admin_ReproducesTheFileExactly()
    {
        // The join is lossless when nothing is dropped: a reader who sees everything must get
        // byte-for-byte what GitHub served, or the filter is rewriting content rather than
        // selecting it.
        var result = Apply(Sample, Roles(isCoord: false, RoleNames.Admin));

        result.Should().Be(Sample);
    }

    [HumansFact]
    public void Apply_Board_SeesAllBoardAdminBlocksRegardlessOfParenthetical()
    {
        const string mixed = """
            ## As a Board member / Admin

            Plain-body

            ## As a Board member / Admin (Camps Admin)

            Camp-scoped-body
            """;

        var result = Apply(mixed, Roles(isCoord: false, RoleNames.Board));

        result.Should().Contain("Plain-body");
        result.Should().Contain("Camp-scoped-body");
    }

    [HumansFact]
    public void Apply_AdminOnFileWithNoBoardAdminBlock_StillSeesCoordinator()
    {
        // Every other Admin/Board case in this file also contains a boardadmin block, so the
        // within-file superset promotion could carry them and IsCoordinatorVisible's own
        // Board/Admin grant would never be exercised. This file has no boardadmin block at all.
        const string coordOnly = """
            ## As a Coordinator

            Coord-only content.
            """;

        var result = Apply(coordOnly, Roles(isCoord: false, RoleNames.Admin));

        result.Should().Contain("Coord-only content.");
    }

    [HumansFact]
    public void Apply_CampLead_SeesCampLeadBlock()
    {
        // The Camps Coordinator block is written for camp leads, who hold no system role.
        var result = Apply(CampsLike, CampLeadOnly());

        result.Should().Contain("Camp lead content.");
        result.Should().NotContain("Camp admin content.");
    }

    [HumansFact]
    public void Apply_CampLead_DoesNotSeeUnrelatedCoordinatorBlocks()
    {
        // Leading a camp is not a general coordinator grant: only blocks whose parenthetical
        // names Camp Lead open up.
        var result = Apply(Sample, CampLeadOnly());

        result.Should().Contain("Volunteer content.");
        result.Should().NotContain("Coord content.");
    }

    [HumansFact]
    public void Apply_NotACampLead_DoesNotSeeCampLeadBlock()
    {
        var result = Apply(CampsLike, Roles(isCoord: false));

        result.Should().NotContain("Camp lead content.");
    }

    [HumansFact]
    public void Apply_NoRoleHeadings_ReturnsUnchanged()
    {
        const string plain = "Glossary entries.";

        var result = Apply(plain, GuideRoleContext.Anonymous);

        result.Should().Be(plain);
    }
}
