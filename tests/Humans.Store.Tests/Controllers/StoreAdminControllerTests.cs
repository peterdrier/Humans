using AwesomeAssertions;
using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Store.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Store.Tests.Controllers;

public sealed class StoreAdminControllerTests
{
    [HumansTheory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.StoreAdmin, true)]
    [InlineData(RoleNames.FinanceAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    public async Task Order_year_actions_enforce_store_admin_access(string? role, bool allowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options => new SectionPolicies().AddPolicies(options));
        using var provider = services.BuildServiceProvider();
        var identity = role is null ? new ClaimsIdentity() : new ClaimsIdentity("test");
        if (!string.IsNullOrEmpty(role))
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        var user = new ClaimsPrincipal(identity);
        var policy = typeof(StoreAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().Single().Policy!;

        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, resource: null, policy);

        result.Succeeded.Should().Be(allowed);
    }

    [HumansFact]
    public void Controller_requires_store_admin_policy()
    {
        var attribute = typeof(StoreAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        attribute.Policy.Should().Be(PolicyNames.StoreCatalogAdmin);
    }

    [HumansFact]
    public void RepairOrderYears_requires_post_and_antiforgery()
    {
        var method = typeof(StoreAdminController).GetMethod(nameof(StoreAdminController.RepairOrderYears));

        method.Should().NotBeNull();
        method!.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Should().ContainSingle();
        method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: false).Should().ContainSingle();
    }
}
