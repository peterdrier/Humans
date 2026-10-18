using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using UserService = Humans.Users.Services.UserService;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;

namespace Humans.Users.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository pattern for the User section.
/// </summary>
public class UserArchitectureTests
{
    // ── Public faces share one instance ──────────────────────────────────────

    [HumansFact]
    public void IUserManagement_IUserService_IUserWrite_And_IUserInfoInvalidator_ResolveToSameSingleton()
    {
        // The real section registration: the same CachingUserService singleton
        // must back every interface key, or an external "user changed"
        // signal misses the cache owner.
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());
        services.AddLogging();

        using var provider = services.BuildServiceProvider();

        var fromManagement = provider.GetRequiredService<IUserManagement>();
        var fromRead = provider.GetRequiredService<IUserService>();
        var fromWrite = provider.GetRequiredService<IUserWrite>();
        var fromInvalidator = provider.GetRequiredService<IUserInfoInvalidator>();
        var concrete = provider.GetRequiredService<CachingUserService>();

        ReferenceEquals(fromManagement, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
        ReferenceEquals(fromWrite, concrete).Should().BeTrue();
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
