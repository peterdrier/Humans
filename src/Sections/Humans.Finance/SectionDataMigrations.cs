using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Finance.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Finance;

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

    // Masked: this screen lists every member at once; an unmasked IBAN is shown one member at a time, behind the
    // audited reveal.
    private static async Task<DataMigrationStatus> IbanStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Burner name", "IBAN"],
            [.. (await sp.GetRequiredService<IPaymentDetailsManagement>().GetUncopiedLegacyIbansAsync(ct))
                .Select(r => (IReadOnlyList<string>)[r.UserId.ToString(), r.BurnerName, IbanFormatter.Mask(r.Iban)])]);

    private static Task<int> ApplyIbanAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IPaymentDetailsManagement>().CopyLegacyIbansAsync(ct);
}
