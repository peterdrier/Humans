using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.MailerLite;

/// <summary>The env var is the production path (Coolify can't set dotted keys); the dotted key is for dev.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "MailerLite:ApiKey", "MailerLite", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        registry.RegisterEnvironmentVariable("MAILERLITE_API_KEY", "MailerLite", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
    }
}
