using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using GoogleWorkspaceUserService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceUserService;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the migrated Google Integration
/// <see cref="GoogleWorkspaceUserService"/>. The service is a thin orchestrator
/// over <see cref="IWorkspaceUserDirectoryClient"/>; these tests pin down the
/// dispatch contract (connector calls happen with the expected arguments) and
/// the only piece of real behavior owned by the service — the
/// <c>lastName</c> pre-flight guard from the pre-§15 implementation.
/// </summary>
public class GoogleWorkspaceUserServiceTests
{
    private readonly IWorkspaceUserDirectoryClient _client;
    private readonly GoogleWorkspaceUserService _service;

    public GoogleWorkspaceUserServiceTests()
    {
        _client = Substitute.For<IWorkspaceUserDirectoryClient>();
        _service = new GoogleWorkspaceUserService(
            _client, NullLogger<GoogleWorkspaceUserService>.Instance);
    }

    [HumansFact]
    public async Task ListAccountsAsync_DelegatesToClient()
    {
        IReadOnlyList<WorkspaceUserAccount> expected =
        [
            new("a@nobodies.team", "A", "Alpha", false, DateTime.UtcNow, null)
        ];
        _client.ListAccountsAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _service.ListAccountsAsync();

        result.Should().BeSameAs(expected);
    }

    [HumansFact]
    public async Task GetAccountAsync_DelegatesToClient_AndReturnsNullWhenMissing()
    {
        _client.GetAccountAsync("missing@nobodies.team", Arg.Any<CancellationToken>())
            .Returns((WorkspaceUserAccount?)null);

        var result = await _service.GetAccountAsync("missing@nobodies.team");

        result.Should().BeNull();
        await _client.Received(1).GetAccountAsync("missing@nobodies.team", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ProvisionAccountAsync_ForwardsAllArguments()
    {
        var expected = new WorkspaceUserAccount(
            "new@nobodies.team", "New", "User", false, DateTime.UtcNow, null);
        _client.ProvisionAccountAsync(
                "new@nobodies.team", "New", "User", "TempP@ss", "recover@example.com",
                Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.ProvisionAccountAsync(
            "new@nobodies.team", "New", "User", "TempP@ss", "recover@example.com");

        result.Should().BeSameAs(expected);
        await _client.Received(1).ProvisionAccountAsync(
            "new@nobodies.team", "New", "User", "TempP@ss", "recover@example.com",
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ProvisionAccountAsync_ThrowsWhenLastNameBlank(string? lastName)
    {
        var act = () => _service.ProvisionAccountAsync(
            "new@nobodies.team", "First", lastName!, "pw");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FamilyName is required*");
        await _client.DidNotReceiveWithAnyArgs().ProvisionAccountAsync(
            default!, default!, default!, default!, default, default);
    }

    [HumansFact]
    public async Task SuspendAccountAsync_DelegatesToClient()
    {
        await _service.SuspendAccountAsync("target@nobodies.team");

        await _client.Received(1).SuspendAccountAsync(
            "target@nobodies.team", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReactivateAccountAsync_DelegatesToClient()
    {
        await _service.ReactivateAccountAsync("target@nobodies.team");

        await _client.Received(1).ReactivateAccountAsync(
            "target@nobodies.team", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResetPasswordAsync_DelegatesToClient()
    {
        await _service.ResetPasswordAsync("target@nobodies.team", "NewP@ss");

        await _client.Received(1).ResetPasswordAsync(
            "target@nobodies.team", "NewP@ss", Arg.Any<CancellationToken>());
    }
}
