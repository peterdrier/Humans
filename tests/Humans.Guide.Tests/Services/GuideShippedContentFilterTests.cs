using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Guide.Services;

namespace Humans.Guide.Tests.Services;

/// <summary>
/// The leak check, run over every file that actually ships. Under the old HTML filter this
/// could only be approximated — a test asserted that Markdig emitted no nested
/// <c>&lt;div&gt;</c> inside a role block, because a nested one truncated the regex match and
/// leaked the tail of the block. Filtering markdown removes that failure mode, so the property
/// can now be stated directly: an anonymous reader never receives a role heading written for
/// somebody else.
/// </summary>
public class GuideShippedContentFilterTests
{
    private static readonly Regex PrivilegedHeading = new(
        @"^##\s+As\s+an?\s+(?:\[)?(?:Coordinator|Board)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    [HumansFact]
    public void Anonymous_ReceivesNoCoordinatorOrBoardHeading_AcrossTheWholeCorpus()
    {
        var leaks = new List<string>();

        foreach (var file in Directory.GetFiles(GuideDir(), "*.md"))
        {
            var document = GuideSegmenter.Segment(File.ReadAllText(file));
            var visible = GuideFilter.Apply(document, GuideRoleContext.Anonymous);

            foreach (var line in visible.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                if (PrivilegedHeading.IsMatch(line))
                {
                    leaks.Add($"{Path.GetFileName(file)}: {line}");
                }
            }
        }

        leaks.Should().BeEmpty(
            "a Coordinator or Board/Admin heading reaching an anonymous reader means its block "
            + "was served to everyone");
    }

    [HumansFact]
    public void Admin_ReceivesEveryFileWholeAndUnaltered()
    {
        var context = new GuideRoleContext(
            IsAuthenticated: true,
            IsTeamCoordinator: false,
            IsCampLead: false,
            SystemRoles: new HashSet<string>([Base.Constants.RoleNames.Admin], StringComparer.Ordinal));

        foreach (var file in Directory.GetFiles(GuideDir(), "*.md"))
        {
            var markdown = File.ReadAllText(file);

            GuideFilter.Apply(GuideSegmenter.Segment(markdown), context)
                .Should().Be(markdown, because: $"{Path.GetFileName(file)} must reach an admin intact");
        }
    }

    private static string GuideDir() => Path.Combine(LocateRepoRoot(), "docs", "guide");

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
