using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using GoogleWorkspaceUserService = Humans.GoogleIntegration.Services.GoogleWorkspaceUserService;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// <see cref="GoogleWorkspaceUserService"/> forwards to <see cref="IWorkspaceUserDirectoryClient"/>
/// unchanged. The one rule it owns is the blank-last-name guard: a blank name must never
/// reach Google. The forwarding test is kept for ProvisionAccountAsync alone, whose five
/// same-typed string parameters would let a swapped argument compile silently.
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
    public async Task ProvisionAccountAsync_ForwardsAllArguments()
    {
        var expected = new WorkspaceUserAccount(
            "new@nobodies.team", "New", "User", false, DateTime.UtcNow, null, IsEnrolledIn2Sv: false);
        _client.ProvisionAccountAsync(
                "new@nobodies.team", "New", "User", "TempP@ss", "recover@example.com",
                Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.ProvisionAccountAsync(
            "new@nobodies.team", "New", "User", "TempP@ss", "recover@example.com", TestContext.Current.CancellationToken);

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
            "new@nobodies.team", "First", lastName!, "pw", ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FamilyName is required*");
        await _client.DidNotReceiveWithAnyArgs().ProvisionAccountAsync(
            null!, null!, null!, null!, null, Arg.Any<CancellationToken>());
    }
}
