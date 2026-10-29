using System.Reflection;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Humans.Web.Extensions;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Web.Tests.Filters;

public class AuthorizationPillFilterTests : IDisposable
{
    /// <summary>Policies every active member passes: their pages are not locked, so they show no pill.</summary>
    private static readonly HashSet<string> MemberWidePolicies = new(StringComparer.Ordinal)
    {
        PolicyNames.AppAccess,
        "PreapprovalsAccess",
    };

    private readonly ServiceProvider _serviceProvider;
    private readonly IAuthorizationPolicyProvider _policies;
    private readonly AuthorizationPillFilter _filter;

    public AuthorizationPillFilterTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var roles in SectionDiscoveryExtensions.DiscoverImplementations<ISectionRoles>())
            services.AddSingleton(roles);
        services.AddSingleton(sp => new RoleCatalog(sp.GetServices<ISectionRoles>()));
        services.AddHumansAuthorizationPolicies();
        _serviceProvider = services.BuildServiceProvider();
        _policies = _serviceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        _filter = new AuthorizationPillFilter(_policies, _serviceProvider.GetRequiredService<RoleCatalog>());
    }

    public void Dispose() => _serviceProvider.Dispose();

    /// <summary>A policy the pill can't read leaves its pages without a pill, which is how the old hand-kept list drifted.</summary>
    [HumansFact]
    public async Task Every_policy_on_a_controller_names_who_it_admits()
    {
        var policyNames = ControllerTypes()
            .SelectMany(t => t.GetMethods().Cast<MemberInfo>().Prepend(t))
            .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(a => a.Policy)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        policyNames.Should().HaveCountGreaterThan(20);

        var unreadable = new List<string>();
        foreach (var name in policyNames.Where(n => !MemberWidePolicies.Contains(n)))
        {
            var policy = await _policies.GetPolicyAsync(name);
            if (policy is null || !AuthorizationPillFilter.Admits(policy).Any())
                unreadable.Add(name);
        }

        unreadable.Should().BeEmpty(
            "give a requirement that is not a role check IDescribedRequirement, or list a member-wide policy above");
    }

    [HumansFact]
    public async Task A_role_policy_shows_its_role_titles_without_admin() =>
        (await PillTextAsync("Humans.Rideshare.Controllers.RideshareAdminController", "Index"))
            .Should().Be("Rideshare Admin");

    [HumansFact]
    public async Task A_described_requirement_shows_what_it_admits() =>
        (await PillTextAsync("Humans.Scanner.Controllers.ScannerController", "Index"))
            .Should().Be("Board · Tickets Admin");

    [HumansFact]
    public async Task Admin_alone_shows_admin_only() =>
        (await PillTextAsync("Humans.Debug.Controllers.DebugController", "Logs")).Should().Be("Admin only");

    [HumansFact]
    public async Task A_member_wide_policy_shows_no_pill() =>
        (await PillTextAsync("Humans.Rideshare.Controllers.RideshareController", "Index")).Should().BeNull();

    private Task<string?> PillTextAsync(string controllerFullName, string action)
    {
        var controller = ControllerTypes().Single(t => string.Equals(t.FullName, controllerFullName, StringComparison.Ordinal));
        var method = controller.GetMethods().First(m => string.Equals(m.Name, action, StringComparison.Ordinal));
        return _filter.PillTextAsync(new ControllerActionDescriptor
        {
            MethodInfo = method,
            ControllerTypeInfo = controller.GetTypeInfo(),
        });
    }

    private static IEnumerable<Type> ControllerTypes() =>
        new[] { typeof(HomeController).Assembly }
            .Concat(SectionDiscoveryExtensions.SectionAssemblies())
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);
}
