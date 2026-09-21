using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Email;

/// <summary>Email's own settings. Discovered by Shell — nothing names it.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetRequiredSetting(registry, "Email:SmtpHost", "Email");
        configuration.GetOptionalSetting(registry, "Email:Username", "Email", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Email:Password", "Email", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetRequiredSetting(registry, "Email:FromAddress", "Email");
        configuration.GetRequiredSetting(registry, "Email:BaseUrl", "Email");
        configuration.GetOptionalSetting(registry, "Email:DpoAddress", "Email",
            importance: ConfigurationImportance.Recommended);
    }
}
