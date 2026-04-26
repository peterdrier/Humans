using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Application.Tests.Infrastructure.GoogleWorkspace;

/// <summary>
/// Contract tests for <see cref="StubGoogleGroupProvisioningClient"/>. Pins
/// down the group-lifecycle semantics shared with the real
/// <see cref="GoogleGroupProvisioningClient"/>: creates allocate a numeric
/// id, duplicate creates report HTTP 409, lookups round-trip, and
/// settings-get returns what settings-update wrote.
/// </summary>
public class StubGoogleGroupProvisioningClientTests
{
    private readonly StubGoogleGroupProvisioningClient _client =
        new(NullLogger<StubGoogleGroupProvisioningClient>.Instance);

    private static GroupSettingsExpected BuildExpected() => new(
        WhoCanJoin: "INVITED_CAN_JOIN",
        WhoCanViewMembership: "ALL_MEMBERS_CAN_VIEW",
        WhoCanContactOwner: "ALL_IN_DOMAIN_CAN_CONTACT",
        WhoCanPostMessage: "ALL_IN_DOMAIN_CAN_POST",
        WhoCanViewGroup: "ALL_MEMBERS_CAN_VIEW",
        WhoCanModerateMembers: "OWNERS_AND_MANAGERS",
        AllowExternalMembers: false,
        IsArchived: true,
        MembersCanPostAsTheGroup: true,
        IncludeInGlobalAddressList: true,
        AllowWebPosting: true,
        MessageModerationLevel: "MODERATE_NONE",
        SpamModerationLevel: "MODERATE",
        EnableCollaborativeInbox: true);

    [HumansFact]
    public async Task CreateGroupAsync_NewGroup_ReturnsNumericId()
    {
        var result = await _client.CreateGroupAsync(
            "team-a@nobodies.team", "Team A", "Team A group");

        result.Error.Should().BeNull();
        result.GroupNumericId.Should().NotBeNullOrEmpty();
    }

    [HumansFact]
    public async Task CreateGroupAsync_Duplicate_ReportsConflict()
    {
        await _client.CreateGroupAsync(
            "team-a@nobodies.team", "Team A", "Team A group");

        var second = await _client.CreateGroupAsync(
            "team-a@nobodies.team", "Team A", "Team A group");

        second.GroupNumericId.Should().BeNull();
        second.Error.Should().NotBeNull();
        second.Error!.StatusCode.Should().Be(409,
            because: "mirrors Google's behaviour when a group with the same email already exists");
    }

    [HumansFact]
    public async Task LookupGroupIdAsync_ExistingGroup_RoundTripsId()
    {
        var created = await _client.CreateGroupAsync(
            "team-b@nobodies.team", "Team B", "Team B group");

        var lookup = await _client.LookupGroupIdAsync("team-b@nobodies.team");

        lookup.Error.Should().BeNull();
        lookup.GroupNumericId.Should().Be(created.GroupNumericId);
    }

    [HumansFact]
    public async Task LookupGroupIdAsync_Missing_Returns404()
    {
        var result = await _client.LookupGroupIdAsync("nope@nobodies.team");

        result.GroupNumericId.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task GetGroupSettingsAsync_BeforeUpdate_Returns404()
    {
        var result = await _client.GetGroupSettingsAsync("team-c@nobodies.team");

        result.Settings.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task UpdateThenGet_RoundTripsExpectedFields()
    {
        var expected = BuildExpected();
        var updateError = await _client.UpdateGroupSettingsAsync("team-d@nobodies.team", expected);

        updateError.Should().BeNull();

        var fetched = await _client.GetGroupSettingsAsync("team-d@nobodies.team");
        fetched.Error.Should().BeNull();
        fetched.Settings.Should().NotBeNull();
        fetched.Settings!.WhoCanJoin.Should().Be(expected.WhoCanJoin);
        fetched.Settings.AllowExternalMembers.Should().Be("false",
            because: "booleans round-trip as Google's string representation");
        fetched.Settings.IsArchived.Should().Be("true");
        fetched.Settings.MessageModerationLevel.Should().Be(expected.MessageModerationLevel);
        fetched.Settings.EnableCollaborativeInbox.Should().Be("true");
    }
}
