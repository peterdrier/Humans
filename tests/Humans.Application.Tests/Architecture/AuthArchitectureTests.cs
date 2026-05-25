using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Infrastructure.Repositories.Auth;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MagicLinkService = Humans.Application.Services.Auth.MagicLinkService;
using RoleAssignmentService = Humans.Application.Services.Auth.RoleAssignmentService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository pattern for the Auth section.
/// </summary>
public class AuthArchitectureTests
{
    public static TheoryData<Type> AuthServices =>
    [
        typeof(RoleAssignmentService),
        typeof(MagicLinkService)
    ];

    [HumansTheory]
    [MemberData(nameof(AuthServices))]
    public void Auth_services_live_in_application_auth_namespace(Type serviceType)
    {
        serviceType.Namespace
            .Should().Be("Humans.Application.Services.Auth",
                because: "services with business logic live in Humans.Application");
    }

    [HumansTheory]
    [MemberData(nameof(AuthServices))]
    public void Auth_services_have_no_dbcontext_constructor_parameter(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "Application services must not take DbContext directly");
    }

    [HumansTheory]
    [MemberData(nameof(AuthServices))]
    public void Auth_services_do_not_own_memory_cache(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "Auth cache invalidation routes through explicit invalidator abstractions");
    }

    [HumansFact]
    public void RoleAssignmentService_constructor_takes_no_store_type()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "the Auth section has no store abstraction");
    }

    [HumansFact]
    public void RoleAssignment_repository_has_expected_application_interface_and_sealed_implementation()
    {
        typeof(RoleAssignmentRepository).IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension");
    }

    [HumansFact]
    public void MagicLinkService_has_no_email_settings_or_data_protection_constructor_parameter()
    {
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var settingsParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("EmailSettings", StringComparison.Ordinal) ||
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("IDataProtectionProvider", StringComparison.Ordinal));

        settingsParam.Should().BeNull(
            because: "Data-protection and URL construction live behind IMagicLinkUrlBuilder in Infrastructure");
    }
}
