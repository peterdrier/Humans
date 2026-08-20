using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Humans.Guide.Controllers;
using Humans.Guide.Services;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Guide.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Guide
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Guide had no architecture test file before the move — <c>docs/sections/Guide.md</c>'s
/// touch-and-clean guidance asked for one at migration time, and this is it.
/// </remarks>
public class GuideArchitectureTests
{
    [HumansFact]
    public void RoleResolverReadsTeamsViaTheReadInterface()
    {
        var paramTypes = typeof(GuideRoleResolver).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamServiceRead));
        paramTypes.Should().NotContain(typeof(ITeamService),
            because: "cross-section team reads must use the read interface (section-read-write-split / HUM0032)");
    }

    [HumansFact]
    public void ContentSourceStaysABaseAbstraction()
    {
        // IGuideContentSource carries the section's name and is not the section's: its
        // signatures name only string, and three of its four consumers are elsewhere (the
        // Agent section's three preload readers, Shell's AgentDocsHealthCheck, and Base's
        // GitHubCommunityKbContentSource). Pinning the namespace here is what stops a later
        // pass "tidying" it into Humans.Guide and forcing Base to reference a section.
        typeof(IGuideContentSource).Assembly.GetName().Name
            .Should().Be("Humans.Base");

        typeof(Section).Assembly.GetTypes()
            .Should().NotContain(t => t.Name == "IGuideContentSource");
    }

    [HumansFact]
    public void RefreshIsAdminOnly_AndReadingIsAnonymous()
    {
        // The section doc's routing table and its "non-Admin users cannot trigger
        // POST /Guide/Refresh" negative access rule, pinned. Nothing else exercises the
        // endpoint, so without this the attribute could be dropped silently.
        Action(nameof(GuideController.Refresh))
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle(a => a.Policy == PolicyNames.AdminOnly);

        foreach (var read in new[] { nameof(GuideController.Index), nameof(GuideController.Document) })
        {
            Action(read).GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
                .Should().ContainSingle(because: $"{read} serves the public guide");
        }
    }

    private static MethodInfo Action(string name) =>
        typeof(GuideController).GetMethod(name)
        ?? throw new InvalidOperationException($"GuideController.{name} not found.");

    [HumansFact]
    public void EveryGuideMarkdownFileIsRegistered_AndEveryRegisteredStemExists()
    {
        // GuideFiles is the whole routing and fetch surface: a page added to docs/guide
        // without an entry 404s in-app, and an entry with no file fails its GitHub fetch on
        // every refresh. Neither shows up until someone visits the page.
        var onDisk = Directory
            .GetFiles(Path.Combine(LocateRepoRoot(), "docs", "guide"), "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        GuideFiles.All.Should().BeEquivalentTo(onDisk);
    }

    [HumansFact]
    public void EveryRoleHeadingParentheticalResolvesToAPrivilege()
    {
        // An unmapped parenthetical is silent: the block renders with data-guide-roles=""
        // and reaches nobody it was written for. "(Camp Coordinator)" sat like that on
        // Camps.md (nobodies-collective/Humans#1035) until someone read the map.
        var unmapped = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(LocateRepoRoot(), "docs", "guide"), "*.md"))
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.TrimEnd('\r');
                var match = GuideMarkdownPreprocessor.RoleHeading.Match(line);
                if (!match.Success || !match.Groups["paren"].Success)
                {
                    continue;
                }

                // "## As a [Coordinator](Glossary.md#coordinator)" — the only parens are a
                // markdown link destination, so the heading carries no role scope.
                var open = match.Groups["paren"].Index - 1;
                if (open >= 1 && line[open - 1] == ']')
                {
                    continue;
                }

                foreach (var token in match.Groups["paren"].Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!GuideRolePrivilegeMap.TryResolve(token, out _))
                    {
                        unmapped.Add($"{Path.GetFileName(file)}: ({token})");
                    }
                }
            }
        }

        unmapped.Should().BeEmpty(
            because: "every guide heading parenthetical must name a key in GuideRolePrivilegeMap");
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
