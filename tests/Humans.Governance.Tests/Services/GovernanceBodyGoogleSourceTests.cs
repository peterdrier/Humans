using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Google.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceBodyGoogleSourceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 21, 12, 0);
    private readonly IApplicationRepository _applications = Substitute.For<IApplicationRepository>();
    private readonly ISystemRoleService _roles = Substitute.For<ISystemRoleService>();
    private readonly GovernanceBodyRepository _bodies = new(new TestDbContextFactory<GovernanceDbContext>(
        TestDbOptions.InMemory<GovernanceDbContext>()));

    private GovernanceBodyGoogleSource Source(IGovernanceBodyRepository? bodies = null) => new(
        bodies ?? _bodies, new GovernanceBodyRosters(_applications, _roles, new FakeClock(Now)),
        NullLogger<GovernanceBodyGoogleSource>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task Each_body_claims_its_group_and_files_for_its_roster()
    {
        var (asociada, colaborador, boardMember) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _applications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([asociada]);
        _applications.GetActiveApprovedTierUserIdsAsync(MembershipTier.Colaborador, Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([colaborador]);
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>()).Returns([boardMember]);
        await StoreAsync(GovernanceBody.Asociados, "asociados", File(GovernanceBody.Asociados, "shared", DrivePermissionLevel.Viewer));
        await StoreAsync(GovernanceBody.Board, "board",
            File(GovernanceBody.Board, "shared", DrivePermissionLevel.ContentManager),
            File(GovernanceBody.Board, "closed", DrivePermissionLevel.None));
        await StoreAsync(GovernanceBody.Colaboradores, null);

        var groups = await Source().GetExpectedAsync(ct: Ct);
        var files = await Source().GetExpectedAccessAsync(ct: Ct);

        groups.Keys.Should().BeEquivalentTo($"asociados@{DomainConstants.GoogleGroupDomain}", $"board@{DomainConstants.GoogleGroupDomain}");
        groups[$"board@{DomainConstants.GoogleGroupDomain}"].Should().Equal(boardMember);
        (await Source().GetExpectedAsync($"ASOCIADOS@{DomainConstants.GoogleGroupDomain}", Ct)).Values.Single().Should().Equal(asociada);
        files["shared"].Access.Should().BeEquivalentTo(new Dictionary<Guid, DrivePermissionLevel>
        {
            [asociada] = DrivePermissionLevel.Viewer,
            [boardMember] = DrivePermissionLevel.ContentManager,
        });
        files["closed"].Access.Should().BeEmpty();
        (await Source().GetExpectedAccessAsync("closed", Ct)).Keys.Should().Equal("closed");
    }

    [HumansFact]
    public async Task A_failed_read_leaves_everything_unclaimed_instead_of_throwing()
    {
        var broken = Substitute.For<IGovernanceBodyRepository>();
        broken.GetAllAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        (await Source(broken).GetExpectedAsync(ct: Ct)).Should().BeEmpty();
        (await Source(broken).GetExpectedAccessAsync(ct: Ct)).Should().BeEmpty();
    }

    private Task StoreAsync(GovernanceBody body, string? prefix, params GovernanceBodyFile[] files) =>
        _bodies.ReplaceAsync(new GovernanceBodyLinks { Body = body, GoogleGroupPrefix = prefix, UpdatedAt = Now, Files = [.. files] }, Ct);

    private static GovernanceBodyFile File(GovernanceBody body, string googleId, DrivePermissionLevel level) => new()
    {
        Id = Guid.NewGuid(),
        Body = body,
        GoogleId = googleId,
        Name = googleId,
        ResourceType = GoogleResourceType.DriveFolder,
        PermissionLevel = level,
    };
}
