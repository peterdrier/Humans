using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;

namespace Humans.Store.Services;

/// <summary>
/// GDPR fan-out contributor for Store (nobodies-collective/Humans#1116). The flagged
/// columns — <c>Invoice.IssuedByUserId</c>, <c>OrderLine.AddedByUserId</c>,
/// <c>Payment.RecordedByUserId</c> — are all operator attribution: which volunteer issued
/// the invoice, added the line, or recorded the payment, not the buyer. An order's
/// counterparty (<c>Order.CounterpartyName</c>/<c>VatId</c>/<c>Address</c>/
/// <c>CountryCode</c>/<c>Email</c>) is free text naming who the invoice is billed to — a
/// camp or department's own billing identity — and carries no FK to <c>Users</c> at all, so
/// it is out of reach of a per-<paramref name="userId"/> contributor by construction. An
/// order is always owned by exactly one of a <c>CampSeason</c> or a <c>Team</c>
/// (<c>Docs/Store.md</c> "Concepts"), never a person directly.
///
/// <para>
/// <see cref="ContributeForUserAsync"/> always returns an empty list: no read path exists
/// for "invoices issued / lines added / payments recorded by this user" today (the
/// repository only loads by order, product or year), and one is not added solely to
/// populate an export slice — see <see cref="ErasureDeclaration"/> for the erasure side.
/// </para>
/// </summary>
internal sealed class StoreGdprContributor : IApplicationService, IUserDataContributor
{
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

    private const string FiscalRetention =
        "Retained in full, nothing erased: an invoice, order line and payment are accounting " +
        "vouchers, and Spanish law requires the books and their supporting documents be kept " +
        "6 years (Código de Comercio Art. 30) and 4 years for tax purposes (Ley 58/2003 Art. " +
        "66) — a factura's own counterparty fields (name, tax id, address) are themselves " +
        "required invoice content, not incidental personal data (Docs/Store.md \"Factura vs " +
        "factura simplificada\"). IssuedByUserId, AddedByUserId and RecordedByUserId are bare " +
        "attribution FKs into Users; Users' own Article 17 erasure anonymizes the account " +
        "they point at, so after that the rows still name a document and an operator, just " +
        "an anonymized one rather than a named person. GDPR Art. 17(3)(b).";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.StoreRecords] = FiscalRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// No-op: nothing on the Store side changes. See <see cref="ErasureDeclaration"/> for why.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
