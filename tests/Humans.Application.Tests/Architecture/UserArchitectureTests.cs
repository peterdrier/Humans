using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Users;
using Humans.Infrastructure.Services.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using AccountProvisioningService = Humans.Application.Services.Users.AccountProvisioningService;
using UnsubscribeService = Humans.Application.Services.Users.UnsubscribeService;
using UserService = Humans.Application.Services.Users.UserService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository pattern for the User section.
/// </summary>
public class UserArchitectureTests
{
    public static TheoryData<Type> UserServices =>
    [
        typeof(UserService),
        typeof(AccountProvisioningService),
        typeof(UnsubscribeService)
    ];

    public static TheoryData<Type, Type> RequiredConstructorEdges => new()
    {
        { typeof(UserService), typeof(IUserRepository) },
        { typeof(AccountProvisioningService), typeof(IUserRepository) },
        { typeof(AccountProvisioningService), typeof(IUserEmailService) },
        { typeof(UnsubscribeService), typeof(IUserRepository) },
    };

    public static TheoryData<Type, Type> ForbiddenConstructorEdges => new()
    {
        { typeof(AccountProvisioningService), typeof(IUserEmailRepository) },
    };

    [HumansTheory]
    [MemberData(nameof(UserServices))]
    public void User_services_live_in_application_users_namespace(Type serviceType)
    {
        serviceType.Namespace
            .Should().Be("Humans.Application.Services.Users",
                because: "User-section services live in Application");
    }

    [HumansTheory]
    [MemberData(nameof(UserServices))]
    public void User_services_have_no_dbcontext_constructor_parameter(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "Application services must use repositories instead of DbContext directly");
    }

    [HumansTheory]
    [MemberData(nameof(RequiredConstructorEdges))]
    public void User_services_take_required_constructor_edges(Type serviceType, Type dependencyType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(dependencyType);
    }

    [HumansTheory]
    [MemberData(nameof(ForbiddenConstructorEdges))]
    public void User_services_do_not_take_forbidden_constructor_edges(Type serviceType, Type dependencyType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().NotContain(dependencyType);
    }

    [HumansFact]
    public void UserService_has_expected_cache_and_invalidation_shape()
    {
        var ctor = typeof(UserService).GetConstructors().Single();
        var parameters = ctor.GetParameters();
        var paramTypes = parameters.Select(p => p.ParameterType).ToList();
        var cachingParam = parameters
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "canonical User data is not IMemoryCache-backed");
        paramTypes.Should().Contain(typeof(IUserInfoInvalidator),
            because: "User writes that change UserInfo-visible fields must invalidate the UserInfo cache");
    }

    [HumansFact]
    public void UserService_has_no_store_serviceprovider_or_higher_level_section_edges()
    {
        var ctor = typeof(UserService).GetConstructors().Single();
        var parameters = ctor.GetParameters();
        var paramTypes = parameters.Select(p => p.ParameterType).ToList();

        parameters.Should().NotContain(
            p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal),
            because: "User has no store abstraction");
        paramTypes.Should().NotContain(typeof(IServiceProvider),
            because: "lazy IServiceProvider escape hatches hide DI cycles");
        paramTypes.Should().NotContain(typeof(ITeamService));
        paramTypes.Should().NotContain(typeof(IRoleAssignmentService));
        paramTypes.Should().NotContain(typeof(IShiftSignupService));
        paramTypes.Should().NotContain(typeof(IShiftManagementService));
        paramTypes.Should().NotContain(typeof(IProfileService));
    }

    [HumansFact]
    public void User_repository_has_expected_application_interface_and_sealed_implementation()
    {
        // Repositories are internal sealed since issue #750 (HumansDbContext
        // sealed). Use GetTypes() — Humans.Application.Tests has
        // InternalsVisibleTo on Humans.Infrastructure.
        var repoType = typeof(IUserRepository).Assembly
            .GetTypes()
            .Concat(typeof(UserRepository).Assembly.GetTypes())
            .Single(t => string.Equals(t.Name, "UserRepository", StringComparison.Ordinal)
                         && typeof(IUserRepository).IsAssignableFrom(t));

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension");
    }

    [HumansFact]
    public void NoOAuthTokenInUserEmailServiceOrRepositoryMethodNames()
    {
        // This test scans METHOD NAMES on the service/repo interfaces — it
        // enforces "don't bake provider-specific verbs into the surface".
        // ReconcileOAuthIdentityAsync (issue nobodies-collective/Humans#697)
        // is the one allowed exception: "OAuth" here is categorical (the
        // OAuth-callback write channel, distinct from user-driven email
        // management), not provider-specific. The orthogonal "only
        // AccountController may CALL ReconcileOAuthIdentityAsync" caller
        // restriction is the Roslyn analyzer pin tracked in #695 — not in
        // scope for this test.
        var allow = new HashSet<string>(StringComparer.Ordinal)
        {
            "ReconcileOAuthIdentityAsync",
        };

        var offenders = new List<string>();
        var typesToScan = new[]
        {
            typeof(IUserEmailService),
            typeof(Humans.Infrastructure.Repositories.Profiles.UserEmailRepository),
            typeof(IUserEmailRepository),
        };

        foreach (var t in typesToScan)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!allow.Contains(m.Name)
                    && m.Name.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{t.Name}.{m.Name} (method)");
            }

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.Name.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{t.Name}.{p.Name} (property)");
            }
        }

        offenders.Should().BeEmpty(
            because: "provider-specific operations are parameterized via a Provider arg. Offenders: {0}",
            string.Join("; ", offenders));
    }

    // ── IUserServiceRead split (memory/architecture/section-read-write-split.md) ──

    [HumansFact]
    public void IUserService_InheritsIUserServiceRead()
    {
        typeof(IUserServiceRead).IsAssignableFrom(typeof(IUserService))
            .Should().BeTrue(
                because: "IUserService is the full Users surface; external sections inject the narrow IUserServiceRead. " +
                         "See memory/architecture/section-read-write-split.md.");
    }

    [HumansFact]
    public void CachingUserService_ImplementsIUserServiceRead()
    {
        typeof(IUserServiceRead).IsAssignableFrom(typeof(CachingUserService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void IUserService_And_IUserServiceRead_ResolveToSameSingleton()
    {
        // Mirrors the Users-section DI shape: the same CachingUserService
        // singleton is exposed under both interface keys.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserRepository>());
        services.AddSingleton(Substitute.For<IUserEmailRepository>());
        services.AddSingleton(Substitute.For<IProfileRepository>());
        services.AddSingleton(Substitute.For<IContactFieldRepository>());
        services.AddSingleton(Substitute.For<ICommunicationPreferenceRepository>());
        services.AddSingleton(Substitute.For<IServiceScopeFactory>());
        services.AddSingleton(Substitute.For<ILogger<CachingUserService>>());

        services.AddSingleton<CachingUserService>();
        services.AddSingleton<IUserService>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserServiceRead>(sp => sp.GetRequiredService<CachingUserService>());

        using var provider = services.BuildServiceProvider();

        var fromFull = provider.GetRequiredService<IUserService>();
        var fromRead = provider.GetRequiredService<IUserServiceRead>();
        var concrete = provider.GetRequiredService<CachingUserService>();

        ReferenceEquals(fromFull, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
    }

    [HumansFact]
    public void User_HasNoCrossDomainNavigationProperties()
    {
        var userType = typeof(Humans.Domain.Entities.User);
        var declaredProps = userType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var forbidden = new[]
        {
            "Profile",
            "RoleAssignments",
            "ConsentRecords",
            "Applications",
            "TeamMemberships",
            "CommunicationPreferences",
        };

        forbidden.Where(declaredProps.Contains).Should().BeEmpty(
            because: "cross-section access goes through the owning section service");
        userType
            .GetMethod("GetEffectiveEmail", BindingFlags.Public | BindingFlags.Instance)
            .Should().BeNull(
                because: "User.Email owns the effective-email computation");
    }
}
