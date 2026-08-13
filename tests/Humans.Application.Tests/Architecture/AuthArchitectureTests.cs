using AwesomeAssertions;
using MagicLinkService = Humans.Application.Services.Auth.MagicLinkService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the half of the Auth section that stayed in Base.
/// </summary>
/// <remarks>
/// <c>MagicLinkService</c> is Auth's cross-section orchestrator: it calls no repository and
/// injects <c>Humans.Email.Contracts</c>, a vertical section's leaf, which a horizontal
/// section may not reference (<c>peters-hard-rules.md</c>). It therefore stayed here at
/// Auth's G5 (nobodies-collective/Humans#866) while the role-assignment half moved to
/// <c>Humans.Auth</c>; that half's rules are in <c>AuthArchitectureTests</c> over there.
/// </remarks>
public class AuthArchitectureTests
{
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

    [HumansFact]
    public void MagicLinkService_calls_no_repository()
    {
        // The reason it stayed in Base: an orchestrator by the hard rules' own definition.
        // If a repository ever appears here, the service belongs inside a section and this
        // file is the wrong home for it.
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var repositoryParam = ctor.GetParameters()
            .FirstOrDefault(p => p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal));

        repositoryParam.Should().BeNull(
            because: "MagicLinkService is an orchestrator; orchestrators do not call repositories (peters-hard-rules.md)");
    }
}
