using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Services.Profiles;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;
using AccountMergeService = Humans.Application.Services.Profiles.AccountMergeService;
using CommunicationPreferenceService = Humans.Application.Services.Profiles.CommunicationPreferenceService;
using ContactFieldService = Humans.Application.Services.Profiles.ContactFieldService;
using DuplicateAccountService = Humans.Application.Services.Profiles.DuplicateAccountService;
using EmailProblemsService = Humans.Application.Services.Profiles.EmailProblemsService;
using ProfileService = Humans.Application.Services.Profiles.ProfileService;
using UserEmailService = Humans.Application.Services.Profiles.UserEmailService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/store/decorator pattern for the
/// Profile section.
/// </summary>
public class ProfileArchitectureTests
{
    public static TheoryData<Type> ApplicationProfileServices => new()
    {
        typeof(ProfileService),
        typeof(ContactFieldService),
        typeof(UserEmailService),
        typeof(CommunicationPreferenceService),
        typeof(AccountMergeService),
        typeof(DuplicateAccountService),
    };

    public static TheoryData<Type> ServicesWithoutMemoryCache => new()
    {
        typeof(ProfileService),
        typeof(ContactFieldService),
        typeof(UserEmailService),
        typeof(CommunicationPreferenceService),
    };

    public static TheoryData<Type, Type> RequiredRepositoryEdges => new()
    {
        { typeof(ProfileService), typeof(IProfileRepository) },
        { typeof(AccountMergeService), typeof(IAccountMergeRepository) },
    };

    [HumansTheory]
    [MemberData(nameof(ApplicationProfileServices))]
    public void Profile_services_live_in_application_profile_namespace(Type serviceType)
    {
        serviceType.Namespace
            .Should().Be("Humans.Application.Services.Profiles",
                because: "services with business logic live in Humans.Application per design-rules, organized by section");
    }

    [HumansTheory]
    [MemberData(nameof(ApplicationProfileServices))]
    public void Profile_services_have_no_dbcontext_constructor_parameter(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "Application services must use repositories instead of DbContext directly");
    }

    [HumansTheory]
    [MemberData(nameof(ServicesWithoutMemoryCache))]
    public void Profile_services_do_not_own_memory_cache(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is the decorator's concern, not the service's");
    }

    [HumansTheory]
    [MemberData(nameof(RequiredRepositoryEdges))]
    public void Profile_services_take_their_section_repository(Type serviceType, Type repositoryType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(repositoryType);
    }

    [HumansFact]
    public void ProfileService_has_no_outbound_edge_to_teams_or_stores()
    {
        var ctor = typeof(ProfileService).GetConstructors().Single();
        var parameters = ctor.GetParameters();
        var paramTypes = parameters.Select(p => p.ParameterType).ToList();

        paramTypes.Should().NotContain(typeof(ITeamService),
            because: "Profile is foundational; team deletion cascade is owned elsewhere");
        parameters.Should().NotContain(
            p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal),
            because: "Profile services must not depend on store abstractions");
    }

    [HumansFact]
    public void CachingProfileService_lives_in_infrastructure_and_implements_public_interfaces()
    {
        typeof(CachingProfileService).Namespace
            .Should().Be("Humans.Infrastructure.Services.Profiles",
                because: "caching decorators live in Infrastructure");
        typeof(CachingProfileService).Should().BeAssignableTo<IProfileService>();
        typeof(CachingProfileService).Should().BeAssignableTo<IFullProfileInvalidator>();
    }

    [HumansFact]
    public void EmailProblemsService_depends_only_on_section_services_not_repositories()
    {
        var ctor = typeof(EmailProblemsService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        var allowed = new[]
        {
            typeof(IProfileService),
            typeof(IUserEmailService),
            typeof(IUserService),
            typeof(IClock)
        };

        paramTypes.Should().OnlyContain(t => allowed.Contains(t),
            "EmailProblemsService must use existing section services, never repositories or DbContext");
    }
}
