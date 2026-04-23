using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Application.Tests.Infrastructure.GoogleWorkspace;

/// <summary>
/// Contract tests for <see cref="StubGoogleGroupMembershipClient"/>. These
/// run without Google credentials and pin down the semantic contract the
/// real <see cref="GoogleGroupMembershipClient"/> also has to honour:
/// idempotent adds, paginated list shape, delete-by-resource-name.
/// </summary>
public class StubGoogleGroupMembershipClientTests
{
    private readonly StubGoogleGroupMembershipClient _client =
        new(NullLogger<StubGoogleGroupMembershipClient>.Instance);

    [Fact]
    public async Task ListMembershipsAsync_EmptyGroup_ReturnsEmptyList()
    {
        var result = await _client.ListMembershipsAsync("group-1");

        result.Error.Should().BeNull();
        result.Memberships.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task CreateMembershipAsync_NewMember_ReturnsAdded()
    {
        var result = await _client.CreateMembershipAsync("group-1", "alice@nobodies.team");

        result.Outcome.Should().Be(GroupMembershipMutationOutcome.Added);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task CreateMembershipAsync_DuplicateMember_ReturnsAlreadyExists()
    {
        await _client.CreateMembershipAsync("group-1", "alice@nobodies.team");

        var second = await _client.CreateMembershipAsync("group-1", "alice@nobodies.team");

        second.Outcome.Should().Be(GroupMembershipMutationOutcome.AlreadyExists,
            because: "the real client treats Google's HTTP 409 'already exists' as an idempotent success");
        second.Error.Should().BeNull();
    }

    [Fact]
    public async Task ListMembershipsAsync_AfterAdds_ReturnsAllMembers()
    {
        await _client.CreateMembershipAsync("group-1", "alice@nobodies.team");
        await _client.CreateMembershipAsync("group-1", "bob@nobodies.team");

        var result = await _client.ListMembershipsAsync("group-1");

        result.Memberships.Should().NotBeNull();
        result.Memberships!.Select(m => m.MemberEmail)
            .Should().BeEquivalentTo(["alice@nobodies.team", "bob@nobodies.team"]);
        result.Memberships!.Should().AllSatisfy(m =>
            m.ResourceName.Should().StartWith("groups/group-1/memberships/",
                because: "the stub's resource-name shape mirrors the real client so deletes flow through unchanged"));
    }

    [Fact]
    public async Task DeleteMembershipAsync_ExistingName_RemovesFromList()
    {
        await _client.CreateMembershipAsync("group-1", "alice@nobodies.team");
        var listBefore = await _client.ListMembershipsAsync("group-1");
        var resourceName = listBefore.Memberships!.Single().ResourceName;

        var deleteError = await _client.DeleteMembershipAsync(resourceName);

        deleteError.Should().BeNull();
        var listAfter = await _client.ListMembershipsAsync("group-1");
        listAfter.Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteMembershipAsync_MissingName_Returns404Error()
    {
        var result = await _client.DeleteMembershipAsync("groups/missing/memberships/nope");

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(404,
            because: "missing memberships surface as 404 errors matching Google's HTTP behaviour");
    }

    [Fact]
    public async Task MembershipOperations_AreIsolatedPerGroup()
    {
        await _client.CreateMembershipAsync("group-1", "alice@nobodies.team");
        await _client.CreateMembershipAsync("group-2", "bob@nobodies.team");

        var g1 = await _client.ListMembershipsAsync("group-1");
        var g2 = await _client.ListMembershipsAsync("group-2");

        g1.Memberships!.Select(m => m.MemberEmail).Should().BeEquivalentTo(["alice@nobodies.team"]);
        g2.Memberships!.Select(m => m.MemberEmail).Should().BeEquivalentTo(["bob@nobodies.team"]);
    }
}
