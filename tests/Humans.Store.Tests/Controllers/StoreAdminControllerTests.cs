using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Store.Contracts;
using AwesomeAssertions;
using System.Security.Claims;
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
    [InlineData(RoleNames.Board, true)]
    [InlineData(StoreRoles.StoreAdmin, true)]
    [InlineData(FinanceRoles.FinanceAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(StoreRoles.StoreAdmin + "x", false)]
    public async Task Admin_controller_reads_for_store_admin_roles_and_Board(string? role, bool allowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options => ((ISectionPolicies)new Section()).AddPolicies(options));
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
    public void RepairOrderYears_requires_post_and_antiforgery()
    {
        var method = typeof(StoreAdminController).GetMethod(nameof(StoreAdminController.RepairOrderYears));

        method.Should().NotBeNull();
        method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Should().ContainSingle();
        method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: false).Should().ContainSingle();
    }
}
