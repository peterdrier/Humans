using System.Reflection;
using AwesomeAssertions;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies CampaignController action authorization wiring.
/// </summary>
public class CampaignControllerTests
{
    [HumansFact]
    public void Class_IsProtected()
    {
        var classAttr = typeof(CampaignController).GetCustomAttribute<AuthorizeAttribute>();
        classAttr.Should().NotBeNull("class-level [Authorize] must protect all campaign actions");
    }

    [HumansFact]
    public void Class_has_expected_route_prefix()
    {
        var routeAttr = typeof(CampaignController).GetCustomAttribute<RouteAttribute>();
        routeAttr.Should().NotBeNull();
        routeAttr!.Template.Should().Be("Admin/Campaigns");
    }

    [HumansFact]
    public void Detail_Action_HasTicketAdminOrAdmin_Policy()
    {
        AssertActionPolicy(nameof(CampaignController.Detail), PolicyNames.TicketAdminOrAdmin, typeof(HttpGetAttribute));
    }

    [HumansFact]
    public void GenerateCodes_Action_HasTicketAdminOrAdmin_Policy()
    {
        AssertActionPolicy(nameof(CampaignController.GenerateCodes), PolicyNames.TicketAdminOrAdmin, typeof(HttpPostAttribute));
    }

    [HumansFact]
    public void AdminOnlyActions_Have_AdminOnly_policy()
    {
        AssertActionPolicy(nameof(CampaignController.Index), PolicyNames.AdminOnly, typeof(HttpGetAttribute));
        AssertActionPolicy(nameof(CampaignController.Create), PolicyNames.AdminOnly, typeof(HttpGetAttribute));
        AssertActionPolicy(nameof(CampaignController.Create), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
        AssertActionPolicy(nameof(CampaignController.Edit), PolicyNames.AdminOnly, typeof(HttpGetAttribute));
        AssertActionPolicy(nameof(CampaignController.Edit), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
        AssertActionPolicy(nameof(CampaignController.ImportCodes), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
        AssertActionPolicy(nameof(CampaignController.Activate), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
        AssertActionPolicy(nameof(CampaignController.Complete), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
        AssertActionPolicy(nameof(CampaignController.SendWave), PolicyNames.AdminOnly, typeof(HttpGetAttribute));
        AssertActionPolicy(nameof(CampaignController.SendWave), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
        AssertActionPolicy(nameof(CampaignController.Resend), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
        AssertActionPolicy(nameof(CampaignController.RetryAllFailed), PolicyNames.AdminOnly, typeof(HttpPostAttribute));
    }

    [HumansFact]
    public void PostActions_Have_AntiForgery_Validation()
    {
        AssertActionHasValidateAntiForgery(nameof(CampaignController.Create), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.Edit), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.GenerateCodes), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.ImportCodes), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.Activate), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.Complete), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.SendWave), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.Resend), typeof(HttpPostAttribute));
        AssertActionHasValidateAntiForgery(nameof(CampaignController.RetryAllFailed), typeof(HttpPostAttribute));
    }

    private static MethodInfo GetAction(string actionName, Type httpMethodAttributeType)
    {
        var candidates = typeof(CampaignController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
            .Where(m => m.GetCustomAttribute(httpMethodAttributeType) is not null)
            .ToList();

        candidates.Should().HaveCount(1,
            because: $"{actionName} must be uniquely identified by HTTP method attribute {httpMethodAttributeType.Name}");
        return candidates.Single();
    }

    private static void AssertActionPolicy(string actionName, string policy, Type httpMethodAttributeType)
    {
        var method = GetAction(actionName, httpMethodAttributeType);
        var auth = method.GetCustomAttribute<AuthorizeAttribute>();
        auth.Should().NotBeNull($"{actionName} ({httpMethodAttributeType.Name}) requires [Authorize]");
        auth!.Policy.Should().Be(policy,
            because: $"{actionName} ({httpMethodAttributeType.Name}) must use {policy}");
    }

    private static void AssertActionHasValidateAntiForgery(string actionName, Type httpMethodAttributeType)
    {
        var method = GetAction(actionName, httpMethodAttributeType);
        method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()
            .Should().NotBeNull(
                $"{actionName} ({httpMethodAttributeType.Name}) must validate anti-forgery tokens");
    }
}
