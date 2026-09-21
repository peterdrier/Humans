using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Finance;

/// <summary>
/// Finance's own settings — the organisation's identity in a SEPA payout file, read by
/// /Finance/Sepa/Generate. Without them SEPA generation reports itself unavailable.
/// </summary>
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
        // Hard per-transfer ceiling; defaults to €50 when unset.
        configuration.GetOptionalSetting(registry, "Sepa:MaxPayoutPerTransfer", "SEPA");
        // Booking on /Finance/Sepa: the Holded treasury the payout left from and that bank's
        // ledger account number. Either unset, the screen offers no Book buttons.
        configuration.GetOptionalSetting(registry, "Sepa:TreasuryAccountId", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:TreasuryLedgerAccount", "SEPA",
            importance: ConfigurationImportance.Recommended);

        // IBAN override — env-var wins over the Sepa:CreditorIban appsetting.
        registry.RegisterEnvironmentVariable("SEPA_CREDITOR_IBAN", "SEPA", isSensitive: true);
    }
}
