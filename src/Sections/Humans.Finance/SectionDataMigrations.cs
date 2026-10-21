using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Finance.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Finance;

/// <summary>
/// Finance's data migrations, run from <c>/Debug/DataMigrations</c>. The copy resolves Finance's
/// own service; the Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string IbanToPaymentDetailsKey = "finance.iban-to-payment-details";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(IbanToPaymentDetailsKey, "Finance",
            "Copy each member's IBAN from profiles.Iban into finance_payment_details",
            "PaymentDetailsService's legacy fallback and ProfileInfo.Iban; then a drop PR for profiles.Iban",
            IbanStatusAsync, ApplyIbanAsync),
    ];

    // The IBAN is shown masked: the screen lists every member at once, and an unmasked IBAN is
    // only ever shown one member at a time behind the audited reveal.
    private static async Task<DataMigrationStatus> IbanStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Burner name", "IBAN"],
            [.. (await sp.GetRequiredService<IPaymentDetailsManagement>().GetUncopiedLegacyIbansAsync(ct))
                .Select(r => (IReadOnlyList<string>)[r.UserId.ToString(), r.BurnerName, IbanFormatter.Mask(r.Iban)])]);

    private static Task<int> ApplyIbanAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IPaymentDetailsManagement>().CopyLegacyIbansAsync(ct);
}
