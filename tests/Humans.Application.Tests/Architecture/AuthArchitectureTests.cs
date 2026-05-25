using AwesomeAssertions;
using MagicLinkService = Humans.Application.Services.Auth.MagicLinkService;
using RoleAssignmentService = Humans.Application.Services.Auth.RoleAssignmentService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository pattern for the Auth section.
/// </summary>
public class AuthArchitectureTests
{
    [HumansFact]
    public void RoleAssignmentService_constructor_takes_no_store_type()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "the Auth section has no store abstraction");
    }

    [HumansFact]
    public void MagicLinkService_has_no_email_settings_or_data_protection_constructor_parameter()
    {
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var settingsParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("EmailSettings", StringComparison.Ordinal) ||
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("IDataProtectionProvider", StringComparison.Ordinal));

        settingsParam.Should().BeNull(
            because: "Data-protection and URL construction live behind IMagicLinkUrlBuilder in Infrastructure");
    }
}
