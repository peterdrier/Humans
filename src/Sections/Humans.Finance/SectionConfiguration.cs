using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Finance;

/// <summary>Without these, SEPA generation reports itself unavailable.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Sepa:CreditorName", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:CreditorIban", "SEPA", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:CreditorBic", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:CreditorIdentifier", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:ChargeBearer", "SEPA");
        // Defaults to €50 when unset.
        configuration.GetOptionalSetting(registry, "Sepa:MaxPayoutPerTransfer", "SEPA");
        // Either unset, /Finance/Sepa offers no Book buttons.
        configuration.GetOptionalSetting(registry, "Sepa:TreasuryAccountId", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:TreasuryLedgerAccount", "SEPA",
            importance: ConfigurationImportance.Recommended);

        // Wins over the Sepa:CreditorIban appsetting.
        registry.RegisterEnvironmentVariable("SEPA_CREDITOR_IBAN", "SEPA", isSensitive: true);
    }
}
