using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using UserService = Humans.Users.Services.UserService;
using Humans.Users.Data.Repositories;

namespace Humans.Users.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository pattern for the User section.
/// </summary>
public class UserArchitectureTests
{
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
    public void IUserService_IUserServiceRead_And_IUserInfoInvalidator_ResolveToSameSingleton()
    {
        // Mirrors the Users-section DI shape: the same CachingUserService
        // singleton is exposed under all three interface keys.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserRepository>());
        services.AddSingleton(Substitute.For<ICommunicationPreferenceRepository>());
        services.AddSingleton(Substitute.For<IServiceScopeFactory>());
        services.AddSingleton(Substitute.For<ILogger<CachingUserService>>());

        services.AddSingleton<CachingUserService>();
        services.AddSingleton<IUserService>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserServiceRead>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserInfoInvalidator>(sp => sp.GetRequiredService<CachingUserService>());

        using var provider = services.BuildServiceProvider();

        var fromFull = provider.GetRequiredService<IUserService>();
        var fromRead = provider.GetRequiredService<IUserServiceRead>();
        var fromInvalidator = provider.GetRequiredService<IUserInfoInvalidator>();
        var concrete = provider.GetRequiredService<CachingUserService>();

        ReferenceEquals(fromFull, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
        ReferenceEquals(fromInvalidator, concrete).Should().BeTrue();
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // SharedResource stays allowed: the Common_/Validation_/Admin_/Todo_/Application*_
        // prefixes are rendered by other sections too.
        // Any third set resolves to nothing and shows the key name instead of the text.
        var allowed = new[] { typeof(UsersResource), typeof(SharedResource) };
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && !allowed.Contains(p.ParameterType.GetGenericArguments()[0]))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Users copy lives in UsersResource and the left-behind shared prefixes in "
                   + "SharedResource; resolving a key through any third set renders the key "
                   + "itself and no error");
    }
}
