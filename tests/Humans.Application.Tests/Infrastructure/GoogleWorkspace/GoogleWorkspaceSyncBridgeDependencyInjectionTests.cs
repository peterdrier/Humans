using AwesomeAssertions;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Application.Tests.Infrastructure.GoogleWorkspace;

/// <summary>
/// Resolution-level tests for the §15 Part 2a bridge DI wiring. The full
/// <c>AddGoogleWorkspaceInfrastructure</c> extension pulls in a lot of
/// collaborators; here we register the minimum needed to instantiate the
/// four new bridge implementations. That's enough to prove each client
/// binds to its interface and to the shared credential settings option.
/// </summary>
public class GoogleWorkspaceSyncBridgeDependencyInjectionTests
{
    [Fact]
    public void Stubs_BindToEveryBridgeInterface()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new GoogleWorkspaceSettings()));
        services.AddLogging();

        services.AddSingleton<IGoogleGroupMembershipClient, StubGoogleGroupMembershipClient>();
        services.AddSingleton<IGoogleGroupProvisioningClient, StubGoogleGroupProvisioningClient>();
        services.AddSingleton<IGoogleDrivePermissionsClient, StubGoogleDrivePermissionsClient>();
        services.AddSingleton<IGoogleDirectoryClient, StubGoogleDirectoryClient>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<IGoogleGroupMembershipClient>()
            .Should().BeOfType<StubGoogleGroupMembershipClient>();
        provider.GetRequiredService<IGoogleGroupProvisioningClient>()
            .Should().BeOfType<StubGoogleGroupProvisioningClient>();
        provider.GetRequiredService<IGoogleDrivePermissionsClient>()
            .Should().BeOfType<StubGoogleDrivePermissionsClient>();
        provider.GetRequiredService<IGoogleDirectoryClient>()
            .Should().BeOfType<StubGoogleDirectoryClient>();
    }

    [Fact]
    public void RealImplementations_BindToEveryBridgeInterface()
    {
        // Real implementations don't talk to Google during construction —
        // the underlying SDK client handles are created lazily on first use
        // inside each GetXxxServiceAsync helper. Resolving them is a
        // sufficient structural check.
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new GoogleWorkspaceSettings
        {
            ServiceAccountKeyJson = "{}",
            Domain = "nobodies.team",
            CustomerId = "Ctest"
        }));
        services.AddLogging();

        services.AddSingleton<IGoogleGroupMembershipClient, GoogleGroupMembershipClient>();
        services.AddSingleton<IGoogleGroupProvisioningClient, GoogleGroupProvisioningClient>();
        services.AddSingleton<IGoogleDrivePermissionsClient, GoogleDrivePermissionsClient>();
        services.AddSingleton<IGoogleDirectoryClient, GoogleDirectoryClient>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<IGoogleGroupMembershipClient>()
            .Should().BeOfType<GoogleGroupMembershipClient>();
        provider.GetRequiredService<IGoogleGroupProvisioningClient>()
            .Should().BeOfType<GoogleGroupProvisioningClient>();
        provider.GetRequiredService<IGoogleDrivePermissionsClient>()
            .Should().BeOfType<GoogleDrivePermissionsClient>();
        provider.GetRequiredService<IGoogleDirectoryClient>()
            .Should().BeOfType<GoogleDirectoryClient>();
    }

    [Fact]
    public async Task StubClients_IsolatedInstances_HaveIndependentState()
    {
        // Each test gets a fresh stub so state from one test doesn't bleed
        // into another. Proven by running a mutation in one instance and
        // observing a second instance is untouched.
        var a = new StubGoogleGroupMembershipClient(
            NullLogger<StubGoogleGroupMembershipClient>.Instance);
        var b = new StubGoogleGroupMembershipClient(
            NullLogger<StubGoogleGroupMembershipClient>.Instance);

        await a.CreateMembershipAsync("g", "alice@nobodies.team");

        var bList = await b.ListMembershipsAsync("g");
        bList.Memberships.Should().BeEmpty();
    }
}
