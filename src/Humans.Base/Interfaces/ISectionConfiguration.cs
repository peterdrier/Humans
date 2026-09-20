using Humans.Base.Configuration;
using Microsoft.Extensions.Configuration;

namespace Humans.Base.Interfaces;

/// <summary>
/// The configuration settings a section owns — declared by the section whose code actually
/// reads them, so the admin Configuration page lists every key without Shell, Base or any hub
/// keeping a roll-call of which section uses what. Runs at builder time against the singleton
/// <see cref="ConfigurationRegistry"/>: an implementation only describes its keys, through the
/// <c>GetRequiredSetting</c> / <c>GetOptionalSetting</c> / <c>RegisterEnvironmentVariable</c>
/// extensions, and resolves no services. Not <c>ISectionSettings</c>, which contributes a
/// <c>/Settings</c> tab.
/// </summary>
public interface ISectionConfiguration : ISectionContribution
{
    void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry);
}
