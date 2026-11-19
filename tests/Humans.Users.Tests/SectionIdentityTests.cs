using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Users.Tests;

public sealed class SectionIdentityTests
{
    [HumansFact]
    public void Register_WiresIdentityOverTheSectionsStoresAndClaimsFactory()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Should().Contain(d => d.ServiceType == typeof(UserManager<User>));
        services.Should().Contain(d => d.ServiceType == typeof(IUserClaimsPrincipalFactory<User>)
            && d.ImplementationType == typeof(HumansUserClaimsPrincipalFactory));

        var identity = services.BuildServiceProvider().GetRequiredService<IOptions<IdentityOptions>>().Value;
        identity.User.RequireUniqueEmail.Should().BeFalse(because: "the UserEmail table owns email uniqueness");
        identity.SignIn.RequireConfirmedEmail.Should().BeFalse();
    }
}
