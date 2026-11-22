using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services.Contributors;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Humans.Workgroups.Tests.Services;

/// <summary>
/// The optional email group: Active and Dormant groups claim their current members through the shared group fan-out,
/// and a membership change asks for that group's sync.
/// </summary>
public sealed class WorkgroupGoogleGroupTests : WorkgroupsTestHarness
{
    private WorkgroupGoogleGroupSource NewSource() => new(NewService());

    [HumansFact]
    public async Task Source_ClaimsCurrentMembersOfActiveAndDormantGroupsOnly()
    {
        var active = await SeedWorkgroupAsync(name: "Active", googleGroupPrefix: "wg-active");
        var coordinator = active.Members.Single().UserId;
        var requester = SeedUser("Requester");
        await AddMemberAsync(active.Id, requester, isPending: true);
        var dormant = await SeedWorkgroupAsync(WorkgroupStatus.Dormant, name: "Dormant",
            dormantReason: WorkgroupDormantReason.Delivered, googleGroupPrefix: "wg-dormant");
        await SeedWorkgroupAsync(WorkgroupStatus.Applied, name: "Applied", googleGroupPrefix: "wg-applied");
        await SeedWorkgroupAsync(name: "No group");

        var claims = await NewSource().GetExpectedAsync(ct: Ct);

        claims.Keys.Should().BeEquivalentTo("wg-active@nobodies.team", "wg-dormant@nobodies.team");
        claims["wg-active@nobodies.team"].Should().Equal(coordinator);
        claims["wg-dormant@nobodies.team"].Should().Equal(dormant.Members.Single().UserId);
    }

    [HumansFact]
    public async Task SetGoogleGroup_AcceptsAPastedAddress_AuditsAndRequestsSync()
    {
        var group = await SeedWorkgroupAsync();

        var result = await NewService().SetGoogleGroupAsync(group.Id, SeedUser(), " WG-Finance@nobodies.team ", Ct);

        result.Refusal.Should().BeNull();
        await using var db = OpenContext();
        (await db.Workgroups.SingleAsync(Ct)).GoogleGroupPrefix.Should().Be("wg-finance");
        await AuditLog.Received(1).LogAsync(WorkgroupsAuditActions.WorkgroupGoogleGroupSet, Arg.Any<string>(),
            group.Id, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
        await GoogleGroupSync.Received(1).RequestSyncAsync("wg-finance@nobodies.team", Arg.Any<CancellationToken>(),
            Arg.Any<GoogleSyncSource>());
    }

    [HumansTheory]
    [InlineData("wg@gmail.com")]
    [InlineData("not valid")]
    [InlineData("taken")]
    public async Task SetGoogleGroup_RefusesForeignDomainsBadNamesAndAGroupAlreadyInUse(string prefix)
    {
        await SeedWorkgroupAsync(name: "Other", googleGroupPrefix: "taken");
        var group = await SeedWorkgroupAsync();

        var result = await NewService().SetGoogleGroupAsync(group.Id, SeedUser(), prefix, Ct);

        result.Refusal.Should().NotBeNull();
        await using var db = OpenContext();
        (await db.Workgroups.SingleAsync(w => w.Id == group.Id, Ct)).GoogleGroupPrefix.Should().BeNull();
        GoogleGroupSync.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddingAMember_RequestsTheEmailGroupSync()
    {
        var group = await SeedWorkgroupAsync(googleGroupPrefix: "wg-finance");
        var coordinator = group.Members.Single().UserId;

        await NewService().AddMemberAsync(group.Id, coordinator, SeedUser("New member"), ct: Ct);

        await GoogleGroupSync.Received(1).RequestSyncAsync("wg-finance@nobodies.team", Arg.Any<CancellationToken>(),
            Arg.Any<GoogleSyncSource>());
    }
}
