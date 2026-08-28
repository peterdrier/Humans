using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Settings.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Settings.Tests;

/// <summary>
/// Both screens write the app-wide event values, so both are pinned to
/// <see cref="PolicyNames.AdminOnly"/> — there is no narrower Settings role.
/// </summary>
public sealed class SettingsArchitectureTests
{
    [HumansFact]
    public void AdminSurfaces_RequireTheAdminOnlyPolicy()
    {
        Type[] surfaces = [typeof(SettingsAdminController), typeof(EventSettingsCarryAdminController)];

        foreach (var controller in surfaces)
        {
            var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();
            authorize.Should().NotBeNull(
                because: $"{controller.Name} writes the app-wide event values");
            authorize!.Policy.Should().Be(PolicyNames.AdminOnly,
                because: $"{controller.Name} writes the app-wide event values");
        }
    }
}
