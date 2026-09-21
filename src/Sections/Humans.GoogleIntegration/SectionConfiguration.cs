using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.GoogleIntegration;

/// <summary>
/// GoogleIntegration's own settings: the Workspace service account it provisions with, and
/// the team-resource toggle <c>TeamResourceService</c> binds and reads.
/// </summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:ServiceAccountKeyPath", "Google Workspace",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:ServiceAccountKeyJson", "Google Workspace",
            isSensitive: true, importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:Domain", "Google Workspace",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:CustomerId", "Google Workspace",
            importance: ConfigurationImportance.Recommended);

        // Team Resource Management toggle. Grouped under Teams on the admin page because that
        // is the feature it gates, though this section is what reads it.
        configuration.GetOptionalSetting(registry, "TeamResourceManagement:AllowCoordinatorsToManageResources",
            "Teams");
    }
}
