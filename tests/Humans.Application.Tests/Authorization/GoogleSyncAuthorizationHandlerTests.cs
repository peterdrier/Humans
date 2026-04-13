using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for GoogleSyncAuthorizationHandler — service-layer enforcement
/// for Google Workspace sync operations.
/// Tests cover: system principal bypass, Admin override, TeamsAdmin/Board Preview-only,
/// and denial for unauthorized users on all Execute and Preview operations.
/// </summary>
public sealed class GoogleSyncAuthorizationHandlerTests
{
    private readonly GoogleSyncAuthorizationHandler _handler = new();

    // --- System principal override ---

    [Fact]
    public async Task SystemPrincipal_CanExecute()
    {
        var result = await EvaluateAsync(
            SystemPrincipal.Instance,
            GoogleSyncOperationRequirement.Execute,
            "SyncResourcesByTypeAsync");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task SystemPrincipal_CanPreview()
    {
        var result = await EvaluateAsync(
            SystemPrincipal.Instance,
            GoogleSyncOperationRequirement.Preview,
            "CheckGroupSettingsAsync");
        result.Should().BeTrue();
    }

    // --- Admin override ---

    [Theory]
    [InlineData("SyncResourcesByTypeAsync")]
    [InlineData("AddUserToGroupAsync")]
    [InlineData("RemoveUserFromGroupAsync")]
    [InlineData("ProvisionTeamFolderAsync")]
    [InlineData("EnsureTeamGroupAsync")]
    [InlineData("RemediateGroupSettingsAsync")]
    public async Task Admin_CanExecuteAnyOperation(string operationName)
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var result = await EvaluateAsync(user, GoogleSyncOperationRequirement.Execute, operationName);
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("CheckGroupSettingsAsync")]
    [InlineData("GetEmailMismatchesAsync")]
    [InlineData("GetAllDomainGroupsAsync")]
    public async Task Admin_CanPreview(string operationName)
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var result = await EvaluateAsync(user, GoogleSyncOperationRequirement.Preview, operationName);
        result.Should().BeTrue();
    }

    // --- TeamsAdmin preview access ---

    [Fact]
    public async Task TeamsAdmin_CanPreview()
    {
        var user = CreateUserWithRoles(RoleNames.TeamsAdmin);
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Preview, "SyncResourcesByTypeAsync");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task TeamsAdmin_CannotExecute()
    {
        var user = CreateUserWithRoles(RoleNames.TeamsAdmin);
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Execute, "SyncResourcesByTypeAsync");
        result.Should().BeFalse();
    }

    // --- Board preview access ---

    [Fact]
    public async Task Board_CanPreview()
    {
        var user = CreateUserWithRoles(RoleNames.Board);
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Preview, "CheckGroupSettingsAsync");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Board_CannotExecute()
    {
        var user = CreateUserWithRoles(RoleNames.Board);
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Execute, "RemediateGroupSettingsAsync");
        result.Should().BeFalse();
    }

    // --- Denial cases ---

    [Theory]
    [InlineData(RoleNames.CampAdmin)]
    [InlineData(RoleNames.TicketAdmin)]
    [InlineData(RoleNames.HumanAdmin)]
    [InlineData(RoleNames.FinanceAdmin)]
    [InlineData(RoleNames.VolunteerCoordinator)]
    [InlineData(RoleNames.ConsentCoordinator)]
    public async Task OtherAdminRoles_DeniedForExecute(string roleName)
    {
        var user = CreateUserWithRoles(roleName);
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Execute, "SyncResourcesByTypeAsync");
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(RoleNames.CampAdmin)]
    [InlineData(RoleNames.HumanAdmin)]
    [InlineData(RoleNames.FinanceAdmin)]
    public async Task OtherAdminRoles_DeniedForPreview(string roleName)
    {
        var user = CreateUserWithRoles(roleName);
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Preview, "CheckGroupSettingsAsync");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RegularUser_DeniedForExecute()
    {
        var user = CreateUserWithRoles();
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Execute, "SyncResourcesByTypeAsync");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RegularUser_DeniedForPreview()
    {
        var user = CreateUserWithRoles();
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Preview, "CheckGroupSettingsAsync");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task UnauthenticatedUser_DeniedForExecute()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await EvaluateAsync(
            user, GoogleSyncOperationRequirement.Execute, "SyncResourcesByTypeAsync");
        result.Should().BeFalse();
    }

    // --- Helpers ---

    private async Task<bool> EvaluateAsync(
        ClaimsPrincipal user,
        GoogleSyncOperationRequirement requirement,
        string operationName)
    {
        var context = new AuthorizationHandlerContext(
            [requirement], user, operationName);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
