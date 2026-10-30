using System.Reflection;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Board can see everything and change nothing: each section's Read policy is its Manage policy plus Board, and no
/// write endpoint admits Board outside the places Board legitimately decides.
/// </summary>
public sealed class BoardReadsEverythingTests : IDisposable
{
    /// <summary>
    /// Writes Board makes by design (Governance, workgroups, legal documents), the onboarding review queue (left as it
    /// stands until it is retired), audited reveals that hand out data and change nothing, and the read-only sync
    /// preview. Preapprovals' class gate admits Board to read; its per-program checks deny Board every write (its own
    /// tests prove that). Grants and Surveys still let Board write until Peter decides who owns those writes.
    /// </summary>
    private static readonly HashSet<string> BoardWritesByDesign = new(StringComparer.Ordinal)
    {
        "GovernanceApplicationsController",
        "GovernanceBoardVotingController",
        "GovernanceVotesAdminController",
        "WorkgroupsAdminController",
        "AdminLegalDocumentsController",
        "OnboardingReviewController",
        "CantinaController.RevealHealth",
        "CantinaController.RevealDayHealth",
        "ShiftsController.RevealMedical",
        "GoogleController.SyncPreview",
        "GrantsAdminController",
        "PreapprovalsAdminController",
        "SurveyAdminController",
    };

    private readonly ServiceProvider _serviceProvider;
    private readonly IAuthorizationPolicyProvider _policies;

    public BoardReadsEverythingTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var roles in SectionDiscoveryExtensions.DiscoverImplementations<ISectionRoles>())
            services.AddSingleton(roles);
        services.AddSingleton(sp => new RoleCatalog(sp.GetServices<ISectionRoles>()));
        services.AddHumansAuthorizationPolicies();
        _serviceProvider = services.BuildServiceProvider();
        _policies = _serviceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
    }

    public void Dispose() => _serviceProvider.Dispose();

    public static TheoryData<string> SectionPolicyClasses()
    {
        var data = new TheoryData<string>();
        foreach (var type in PolicyClasses())
            data.Add(type.FullName!);
        return data;
    }

    [HumansFact]
    public void Every_section_admin_role_has_a_policy_class()
    {
        PolicyClasses().Select(t => t.Name).Should().Contain(
            ["UsersPolicies", "TeamsPolicies", "CampsPolicies", "TicketsPolicies", "FinancePolicies",
             "EventsPolicies", "CantinaPolicies", "RidesharePolicies", "StorePolicies", "ShiftsPolicies"]);
    }

    [HumansTheory]
    [MemberData(nameof(SectionPolicyClasses))]
    public async Task Read_is_Manage_plus_Board(string policyClass)
    {
        var type = PolicyClasses().Single(t => string.Equals(t.FullName, policyClass, StringComparison.Ordinal));
        var read = await AdmittedRolesAsync(ConstantValue(type, "Read"));
        var manage = await AdmittedRolesAsync(ConstantValue(type, "Manage"));

        manage.Should().NotContain(RoleNames.Board, $"{type.Name}.Manage is a write gate");
        read.Should().BeEquivalentTo(manage.Append(RoleNames.Board), $"{type.Name}.Read is Manage plus Board");
    }

    [HumansFact]
    public async Task No_write_endpoint_admits_Board_outside_its_own_decisions()
    {
        var violations = new List<string>();
        foreach (var controller in ControllerTypes())
        {
            var classPolicies = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Select(a => a.Policy).OfType<string>().ToList();

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var verbs = action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToList();
                if (verbs.Count == 0 || verbs.All(v => v is "GET" or "HEAD"))
                    continue;
                if (BoardWritesByDesign.Contains(controller.Name)
                    || BoardWritesByDesign.Contains($"{controller.Name}.{action.Name}"))
                    continue;

                var policies = classPolicies
                    .Concat(action.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).OfType<string>())
                    .ToList();
                if (await AdmitsBoardAsRoleAsync(policies))
                    violations.Add($"{controller.Name}.{action.Name} ({string.Join(", ", policies)})");
            }
        }

        string.Join(Environment.NewLine, violations).Should().BeEmpty(
            "Board reads every section and writes only in Governance, workgroups and legal documents");
    }

    /// <summary>
    /// True when at least one requirement restricts by role and every such requirement lets Board through. Endpoints
    /// gated on sign-in or app access alone are member features Board uses as a member.
    /// </summary>
    private async Task<bool> AdmitsBoardAsRoleAsync(IEnumerable<string> policyNames)
    {
        var restricting = 0;
        foreach (var name in policyNames)
        {
            var policy = await _policies.GetPolicyAsync(name)
                ?? throw new InvalidOperationException($"Policy '{name}' is not registered");
            foreach (var roles in policy.Requirements.Select(RolesOf).OfType<IReadOnlyCollection<string>>())
            {
                restricting++;
                if (!roles.Contains(RoleNames.Board, StringComparer.Ordinal))
                    return false;
            }
        }
        return restricting > 0;
    }

    private async Task<IReadOnlyCollection<string>> AdmittedRolesAsync(string policyName)
    {
        var policy = await _policies.GetPolicyAsync(policyName)
            ?? throw new InvalidOperationException($"Policy '{policyName}' is not registered");
        return policy.Requirements.Select(RolesOf).OfType<IReadOnlyCollection<string>>().Single();
    }

    private static IReadOnlyCollection<string>? RolesOf(IAuthorizationRequirement requirement) => requirement switch
    {
        RolesAuthorizationRequirement roles => roles.AllowedRoles.ToList(),
        IDescribedRequirement described => described.Admits,
        _ => null,
    };

    private static string ConstantValue(Type type, string name) =>
        (string)(type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()
            ?? throw new InvalidOperationException($"{type.Name}.{name} is missing"));

    /// <summary>Every <c>&lt;Section&gt;Policies</c> class that has both a Read and a Manage gate.</summary>
    private static IReadOnlyList<Type> PolicyClasses() =>
    [
        .. SectionAndContractAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: true, IsSealed: true, IsPublic: true }
                && t.Name.EndsWith("Policies", StringComparison.Ordinal)
                && t.GetField("Read", BindingFlags.Public | BindingFlags.Static) is not null
                && t.GetField("Manage", BindingFlags.Public | BindingFlags.Static) is not null)
            .DistinctBy(t => t.FullName, StringComparer.Ordinal)
            .OrderBy(t => t.FullName, StringComparer.Ordinal),
    ];

    private static IEnumerable<Assembly> SectionAndContractAssemblies()
    {
        var sections = SectionDiscoveryExtensions.SectionAssemblies().ToList();
        var contracts = sections
            .SelectMany(a => a.GetReferencedAssemblies())
            .Where(n => n.Name?.EndsWith(".Contracts", StringComparison.Ordinal) == true)
            .Select(Assembly.Load);
        return sections.Concat(contracts).Distinct();
    }

    private static IReadOnlyList<Type> ControllerTypes()
    {
        var controllers = new[] { typeof(HomeController).Assembly }
            .Concat(SectionDiscoveryExtensions.SectionAssemblies())
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        controllers.Should().HaveCountGreaterThan(50, "a sweep over few controllers passes vacuously");
        return controllers;
    }
}
