using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Models;
using Humans.Users.Services;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.Services;

public class TeamMessageOptionsProviderTests
{
    private const string InfraUrl = "https://groups.google.com/a/nobodies.team/g/infra";
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 1, 12, 0);

    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly ITeamResourceService _teamResourceService = Substitute.For<ITeamResourceService>();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly TeamMessageOptionsProvider _provider;

    public TeamMessageOptionsProviderTests()
    {
        _provider = new TeamMessageOptionsProvider(_teamService, _teamResourceService);
    }

    [HumansFact]
    public async Task CoordinatorWithSyncedGroup_OffersTheTeam()
    {
        StubTeam(TeamMemberRole.Coordinator);
        StubResources(Resource());

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().ContainSingle()
            .Which.Should().Be(new TeamMessageOption(_teamId, "Infrastructure", "infra@nobodies.team"));
    }

    [HumansFact]
    public async Task NonCoordinator_OffersNothing_WithoutLookingUpResources()
    {
        StubTeam(TeamMemberRole.Member);

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
        await _teamResourceService.DidNotReceiveWithAnyArgs().GetResourcesByTeamIdsAsync(default!, default);
    }

    [HumansFact]
    public async Task InactiveTeam_OffersNothing()
    {
        StubTeam(TeamMemberRole.Coordinator, isActive: false);
        StubResources(Resource());

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
    }

    [HumansFact]
    public async Task TeamWithoutGroupPrefix_OffersNothing()
    {
        StubTeam(TeamMemberRole.Coordinator, groupPrefix: null);
        StubResources(Resource());

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GroupNeverSynced_OffersNothing()
    {
        StubTeam(TeamMemberRole.Coordinator);
        StubResources(Resource(synced: false));

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GroupWithSyncError_OffersNothing()
    {
        StubTeam(TeamMemberRole.Coordinator);
        StubResources(Resource(errorMessage: "Google API error (403)"));

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeactivatedGroup_OffersNothing()
    {
        StubTeam(TeamMemberRole.Coordinator);
        StubResources(Resource(isActive: false));

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DifferentGroup_OffersNothing()
    {
        // Name and GoogleId vary by how the group was linked; the Url is what identifies it.
        StubTeam(TeamMemberRole.Coordinator);
        StubResources(Resource(name: "infra@nobodies.team", url: "https://groups.google.com/a/nobodies.team/g/other"));

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DriveFolderOnly_OffersNothing()
    {
        StubTeam(TeamMemberRole.Coordinator);
        StubResources(Resource(type: GoogleResourceType.DriveFolder, url: "https://drive.google.com/drive/folders/abc"));

        var options = await _provider.GetOptionsAsync(_viewerId, Xunit.TestContext.Current.CancellationToken);

        options.Should().BeEmpty();
    }

    private void StubTeam(TeamMemberRole role, bool isActive = true, string? groupPrefix = "infra") =>
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [_teamId] = new(_teamId, "Infrastructure", null, "infrastructure", isActive, false, SystemTeamType.None, false,
                true, false, false, Now,
                [new(Guid.NewGuid(), _viewerId, "Viewer", "viewer@example.com", null, role, Now)],
                GoogleGroupPrefix: groupPrefix)
        });

    private void StubResources(params GoogleResourceSnapshot[] resources) =>
        _teamResourceService.GetResourcesByTeamIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>> { [_teamId] = resources });

    private GoogleResourceSnapshot Resource(
        string name = "Infrastructure",
        string url = InfraUrl,
        GoogleResourceType type = GoogleResourceType.Group,
        bool synced = true,
        bool isActive = true,
        string? errorMessage = null) =>
        new(Guid.NewGuid(), _teamId, "123456789", name, type, url,
            ProvisionedAt: Now,
            LastSyncedAt: synced ? Now : null,
            IsActive: isActive,
            ErrorMessage: errorMessage);
}
