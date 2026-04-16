using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Infrastructure.Services.Profiles;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ProfileService = Humans.Application.Services.Profile.ProfileService;
using ContactFieldService = Humans.Application.Services.Profile.ContactFieldService;
using UserEmailService = Humans.Application.Services.Profile.UserEmailService;
using CommunicationPreferenceService = Humans.Application.Services.Profile.CommunicationPreferenceService;
using VolunteerHistoryService = Humans.Application.Services.Profile.VolunteerHistoryService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/store/decorator pattern for the
/// Profile section — migrated per PR #504 / §15 Step 0.
///
/// These tests are the enforcement mechanism for §§3–5 of
/// <c>docs/architecture/design-rules.md</c> applied to Profile: if any
/// future change drags the services back into <c>Humans.Infrastructure</c>,
/// reintroduces a <c>DbContext</c> dependency, or accidentally pulls an EF
/// Core reference into <c>Humans.Application</c>, these tests fail loudly.
/// </summary>
public class ProfileArchitectureTests
{
    // ── ProfileService ────────────────────────────────────────────────────────

    [Fact]
    public void ProfileService_LivesInHumansApplicationServicesProfileNamespace()
    {
        typeof(ProfileService).Namespace
            .Should().Be("Humans.Application.Services.Profile",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void ProfileService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(ProfileService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IProfileRepository instead (design-rules §3)");
    }

    [Fact]
    public void ProfileService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(ProfileService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is the decorator's concern (design-rules §5), not the service's");
    }

    [Fact]
    public void ProfileService_TakesRepositoryAndStore()
    {
        var ctor = typeof(ProfileService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IProfileRepository));
        paramTypes.Should().Contain(typeof(IProfileStore));
    }

    // ── IProfileRepository / IProfileStore ───────────────────────────────────

    [Fact]
    public void IProfileRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IProfileRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void IProfileStore_LivesInApplicationInterfacesStoresNamespace()
    {
        typeof(IProfileStore).Namespace
            .Should().Be("Humans.Application.Interfaces.Stores",
                because: "store interfaces live in Humans.Application.Interfaces.Stores per design-rules §4");
    }

    // ── CachingProfileService ─────────────────────────────────────────────────

    [Fact]
    public void CachingProfileService_LivesInHumansInfrastructureServicesProfilesNamespace()
    {
        typeof(CachingProfileService).Namespace
            .Should().Be("Humans.Infrastructure.Services.Profiles",
                because: "caching decorators live in Humans.Infrastructure.Services.{Section} alongside the IMemoryCache-backed invalidators they wrap (design-rules §5)");
    }

    // ── ContactFieldService ───────────────────────────────────────────────────

    [Fact]
    public void ContactFieldService_LivesInHumansApplicationServicesProfileNamespace()
    {
        typeof(ContactFieldService).Namespace
            .Should().Be("Humans.Application.Services.Profile",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void ContactFieldService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(ContactFieldService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IContactFieldRepository instead (design-rules §3)");
    }

    [Fact]
    public void ContactFieldService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(ContactFieldService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is the decorator's concern (design-rules §5), not the service's");
    }

    // ── UserEmailService ──────────────────────────────────────────────────────

    [Fact]
    public void UserEmailService_LivesInHumansApplicationServicesProfileNamespace()
    {
        typeof(UserEmailService).Namespace
            .Should().Be("Humans.Application.Services.Profile",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void UserEmailService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(UserEmailService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IUserEmailRepository instead (design-rules §3)");
    }

    [Fact]
    public void UserEmailService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(UserEmailService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is the decorator's concern (design-rules §5), not the service's");
    }

    // ── CommunicationPreferenceService ────────────────────────────────────────

    [Fact]
    public void CommunicationPreferenceService_LivesInHumansApplicationServicesProfileNamespace()
    {
        typeof(CommunicationPreferenceService).Namespace
            .Should().Be("Humans.Application.Services.Profile",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void CommunicationPreferenceService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(CommunicationPreferenceService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ICommunicationPreferenceRepository instead (design-rules §3)");
    }

    [Fact]
    public void CommunicationPreferenceService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(CommunicationPreferenceService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is the decorator's concern (design-rules §5), not the service's");
    }

    // ── VolunteerHistoryService ───────────────────────────────────────────────

    [Fact]
    public void VolunteerHistoryService_LivesInHumansApplicationServicesProfileNamespace()
    {
        typeof(VolunteerHistoryService).Namespace
            .Should().Be("Humans.Application.Services.Profile",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void VolunteerHistoryService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(VolunteerHistoryService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IVolunteerHistoryRepository instead (design-rules §3)");
    }

    [Fact]
    public void VolunteerHistoryService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(VolunteerHistoryService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is the decorator's concern (design-rules §5), not the service's");
    }
}
