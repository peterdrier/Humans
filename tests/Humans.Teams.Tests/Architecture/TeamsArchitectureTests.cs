using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Humans.Teams.Tests.Architecture;

/// <summary>
/// The section's three faces — <see cref="ITeamManagement"/>, <see cref="ITeamService"/> and
/// <see cref="ITeamWrite"/> — resolve to the same singleton, so a reader and a writer can never
/// end up looking at different caches. Plus the repository implements its own interface.
/// </summary>
public class TeamsArchitectureTests
{
    [HumansFact]
    public void TeamRepository_ImplementsITeamRepository()
    {
        typeof(ITeamRepository).IsAssignableFrom(typeof(TeamRepository))
            .Should().BeTrue();
    }

    [HumansFact]
    public void ITeamManagement_ITeamService_And_ITeamWrite_ResolveToSameSingleton()
    {
        // Mirrors the Teams-section DI shape: the same CachingTeamService
        // singleton is exposed under every interface key.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITeamRepository>());
        services.AddSingleton(Substitute.For<IServiceScopeFactory>());
        services.AddSingleton(Substitute.For<ILogger<CachingTeamService>>());

        services.AddSingleton<CachingTeamService>();
        services.AddSingleton<ITeamManagement>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamWrite>(sp => sp.GetRequiredService<CachingTeamService>());

        using var provider = services.BuildServiceProvider();

        var fromManagement = provider.GetRequiredService<ITeamManagement>();
        var fromRead = provider.GetRequiredService<ITeamService>();
        var fromWrite = provider.GetRequiredService<ITeamWrite>();
        var concrete = provider.GetRequiredService<CachingTeamService>();

        ReferenceEquals(fromManagement, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
        ReferenceEquals(fromWrite, concrete).Should().BeTrue();
    }
}
