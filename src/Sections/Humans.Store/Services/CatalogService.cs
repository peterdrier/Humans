using Humans.Audit.Contracts;
using Humans.Base.Interfaces;
using Humans.Holded.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using NodaTime.Text;
using Humans.Store.Services.Dtos;
using NodaTime;
using System.Globalization;

namespace Humans.Store.Services;

internal sealed class CatalogService(
    IStoreRepository repo,
    IAuditLogService audit,
    IClock clock,
    IHoldedService holded,
    ILogger<CatalogService> logger) : IApplicationService
{
    public async Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
    {
        var products = await repo.GetActiveProductsForYearAsync(year, ct);
        return products
            .Select(MapProduct)
            .ToList();
    }

    public async Task<IReadOnlyList<ProductDto>> GetAllProductsForYearAsync(int year, CancellationToken ct = default)
    {
        var products = await repo.GetAllProductsForYearAsync(year, ct);
        return products
            .Select(MapProduct)
            .ToList();
    }

    public async Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var p = await repo.GetProductByIdAsync(productId, ct);
        return p is null ? null : MapProduct(p);
    }

    public async Task<CatalogSaveResult> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        var refusal = GetProductDraftRefusal(draft) ?? await GetAccountRefusalAsync(draft, current: null, ct);
        if (refusal is not null)
        {
            logger.LogWarning("Store catalog create rejected for actor {ActorId}: {Reason}", actorUserId, refusal);
            return CatalogSaveResult.Failure(null, refusal);
        }

        var now = clock.GetCurrentInstant();
        var product = new Product
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyProductFields(product, draft);
        await repo.AddProductAsync(product, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreProductCreated, StoreAuditSubjectTypes.Product, product.Id,
            $"Created store product '{product.Name}' for year {product.Year}",
            actorUserId);
        return CatalogSaveResult.Success(created: true);
    }

    public async Task<AdminMutationResult> UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        var draftRefusal = GetProductDraftRefusal(draft);
        if (draftRefusal is not null)
            return RefuseCatalogMutation(draft.Id, actorUserId, draftRefusal);

        var product = await repo.GetProductByIdAsync(draft.Id, ct);
        if (product is null)
            return RefuseCatalogMutation(draft.Id, actorUserId, $"Product {draft.Id} not found");

        if (await GetAccountRefusalAsync(draft, product, ct) is { } accountRefusal)
            return RefuseCatalogMutation(draft.Id, actorUserId, accountRefusal);

        var oldPrice = product.UnitPriceEur;

        ApplyProductFields(product, draft);
        product.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateProductAsync(product, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreProductUpdated, StoreAuditSubjectTypes.Product, product.Id,
            $"Updated store product '{product.Name}'",
            actorUserId);

        // A dedicated, queryable price-change event for the order page's audit view.
        if (oldPrice != draft.UnitPriceEur)
            await audit.LogAsync(
                StoreAuditActions.StoreProductPriceChanged, StoreAuditSubjectTypes.Product, product.Id,
                $"Price for {product.Name} changed from {oldPrice:0.00} to {draft.UnitPriceEur:0.00}",
                actorUserId);
        return AdminMutationResult.Success;
    }

    public async Task<CatalogSaveResult> SaveProductWithResultAsync(
        ProductSaveRequest request,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var parseResult = LocalDatePattern.Iso.Parse(request.OrderableUntil ?? string.Empty);
        if (!parseResult.Success)
            return CatalogSaveResult.Failure(nameof(request.OrderableUntil), "Invalid date - use YYYY-MM-DD.");

        var dto = new ProductDto(
            request.Id ?? Guid.Empty,
            request.Year,
            request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.UnitPriceEur,
            request.VatRatePercent,
            request.DepositAmountEur,
            parseResult.Value,
            request.IsActive,
            request.HoldedRevenueAccountNum,
            request.HoldedExpenseAccountNum,
            request.HoldedTransferAccountNum);

        try
        {
            if (request.Id is null)
                return await CreateProductAsync(dto, actorUserId, ct);

            var result = await UpdateProductAsync(dto, actorUserId, ct);
            return result.Succeeded
                ? CatalogSaveResult.Success(created: false)
                : CatalogSaveResult.Failure(null, result.Refusal!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to save Store product {ProductId}", request.Id);
            return new CatalogSaveResult(false, false, null, null);
        }
    }

    public async Task<AdminMutationResult> DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
    {
        var product = await repo.GetProductByIdAsync(productId, ct);
        if (product is null)
            return RefuseCatalogMutation(productId, actorUserId, $"Product {productId} not found");

        product.IsActive = false;
        product.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateProductAsync(product, ct);

        await audit.LogAsync(
            StoreAuditActions.StoreProductDeactivated, StoreAuditSubjectTypes.Product, productId,
            $"Deactivated store product '{product.Name}'",
            actorUserId);
        return AdminMutationResult.Success;
    }

    private AdminMutationResult RefuseCatalogMutation(Guid productId, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store catalog mutation rejected for product {ProductId}, actor {ActorId}: {Reason}",
            productId, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }

    private static void ApplyProductFields(Product product, ProductDto draft)
    {
        product.Year = draft.Year;
        product.Name = draft.Name.Trim();
        product.Description = draft.Description;
        product.UnitPriceEur = draft.UnitPriceEur;
        product.VatRatePercent = draft.VatRatePercent;
        product.DepositAmountEur = draft.DepositAmountEur;
        product.HoldedRevenueAccountNum = draft.HoldedRevenueAccountNum;
        product.HoldedExpenseAccountNum = draft.HoldedExpenseAccountNum;
        product.HoldedTransferAccountNum = draft.HoldedTransferAccountNum;
        product.OrderableUntil = draft.OrderableUntil;
        product.IsActive = draft.IsActive;
    }

    private static string? GetProductDraftRefusal(ProductDto draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            return "Product name is required";
        if (draft.UnitPriceEur < 0m)
            return "Unit price cannot be negative";
        if (draft.VatRatePercent < 0m)
            return "VAT rate cannot be negative";
        if (draft.DepositAmountEur is < 0m)
            return "Deposit cannot be negative";
        if (draft.UnitPriceEur > 9_999_999_999.99m || decimal.Round(draft.UnitPriceEur, 2) != draft.UnitPriceEur)
            return "Unit price must be at most 9,999,999,999.99 with no more than two decimal places";
        if (draft.VatRatePercent > 999.99m || decimal.Round(draft.VatRatePercent, 2) != draft.VatRatePercent)
            return "VAT rate must be at most 999.99 with no more than two decimal places";
        if (draft.DepositAmountEur is { } deposit && (deposit > 9_999_999_999.99m || decimal.Round(deposit, 2) != deposit))
            return "Deposit must be at most 9,999,999,999.99 with no more than two decimal places";
        return null;
    }

    /// <summary>The accounts each product field may hold: the mirror's unarchived chart, one block per field. Reads
    /// the mirror only, never Holded.</summary>
    public async Task<ProductAccountChoices> GetAccountChoicesAsync(CancellationToken ct = default)
    {
        var chart = (await holded.GetChartAsync(ct)).Where(a => !a.Archived).ToList();
        IReadOnlyList<AccountChoice> Block(int from, int to) =>
            chart.Where(a => a.Number >= from && a.Number <= to).Select(a => new AccountChoice(a.Number, a.Name)).ToList();
        return new ProductAccountChoices(
            Block(IncomeBlockStart, IncomeBlockEnd),
            Block(CostBlockStart, CostBlockEnd),
            Block(TeamUsageBlockStart, TeamUsageBlockEnd));
    }

    public async Task<StoreSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var s = await repo.GetSettingsAsync(ct);
        return new StoreSettingsDto(s.StripeGatewayTreasuryId, s.SimplifiedInvoiceThresholdEur);
    }

    public async Task<AdminMutationResult> SaveSettingsAsync(
        StoreSettingsDto settings, Guid actorUserId, CancellationToken ct = default)
    {
        var threshold = settings.SimplifiedInvoiceThresholdEur;
        var gateway = string.IsNullOrWhiteSpace(settings.StripeGatewayTreasuryId) ? null : settings.StripeGatewayTreasuryId.Trim();
        var refusal = threshold < 0m || threshold > 9_999_999_999.99m || decimal.Round(threshold, 2) != threshold
            ? "The simplified invoice threshold must be zero or more, with no more than two decimal places."
            : gateway is { Length: > 64 }
                ? "The Stripe gateway treasury id must be 64 characters or fewer."
                : null;
        if (refusal is not null)
        {
            logger.LogWarning("Store settings save rejected for actor {ActorId}: {Reason}", actorUserId, refusal);
            return AdminMutationResult.Refused(refusal);
        }

        await repo.SaveSettingsAsync(new StoreSettings
        {
            StripeGatewayTreasuryId = gateway,
            SimplifiedInvoiceThresholdEur = threshold,
        }, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreSettingsUpdated, StoreAuditSubjectTypes.Settings, Guid.Empty,
            string.Create(CultureInfo.InvariantCulture,
                $"Store settings: Stripe gateway treasury {gateway ?? "none"}, simplified invoice threshold EUR {threshold:0.00}"),
            actorUserId);
        return AdminMutationResult.Success;
    }

    /// <summary>Sets the three accounts on many products of one year in one save. Any row naming an account the
    /// choices don't hold refuses the whole save; unchanged rows are not written.</summary>
    public async Task<AdminMutationResult> SaveAccountsAsync(
        int year, IReadOnlyList<ProductAccounts> rows, Guid actorUserId, CancellationToken ct = default)
    {
        var products = (await repo.GetAllProductsForYearAsync(year, ct)).ToDictionary(p => p.Id);
        var choices = await GetAccountChoicesAsync(ct);
        var refusals = new List<string>();
        foreach (var row in rows)
        {
            if (!products.TryGetValue(row.ProductId, out var product))
                refusals.Add($"Product {row.ProductId} is not in the {year} catalog.");
            else if (choices.Refusal(
                         Changed(row.Income, product.HoldedRevenueAccountNum),
                         Changed(row.Cost, product.HoldedExpenseAccountNum),
                         Changed(row.TeamUsage, product.HoldedTransferAccountNum)) is { } refusal)
                refusals.Add($"{product.Name}: {refusal}");
        }
        if (refusals.Count > 0)
        {
            logger.LogWarning("Store catalog account save rejected for actor {ActorId}: {Reasons}",
                actorUserId, string.Join(" ", refusals));
            return AdminMutationResult.Refused(string.Join(" ", refusals));
        }

        var now = clock.GetCurrentInstant();
        foreach (var row in rows)
        {
            var product = products[row.ProductId];
            if (product.HoldedRevenueAccountNum == row.Income && product.HoldedExpenseAccountNum == row.Cost
                && product.HoldedTransferAccountNum == row.TeamUsage)
                continue;
            product.HoldedRevenueAccountNum = row.Income;
            product.HoldedExpenseAccountNum = row.Cost;
            product.HoldedTransferAccountNum = row.TeamUsage;
            product.UpdatedAt = now;
            await repo.UpdateProductAsync(product, ct);
            await audit.LogAsync(
                StoreAuditActions.StoreProductUpdated, StoreAuditSubjectTypes.Product, product.Id,
                $"Set accounts on store product '{product.Name}': income {Show(row.Income)}, cost {Show(row.Cost)}, "
                    + $"dept transfers {Show(row.TeamUsage)}",
                actorUserId);
        }
        return AdminMutationResult.Success;

        static string Show(int? account) => account?.ToString(CultureInfo.InvariantCulture) ?? "none";
    }

    /// <summary>Only accounts the save changes are checked: one the product already holds stays saveable after it
    /// drops out of the chart, so it never blocks an unrelated edit.</summary>
    private async Task<string?> GetAccountRefusalAsync(ProductDto draft, Product? current, CancellationToken ct)
    {
        var income = Changed(draft.HoldedRevenueAccountNum, current?.HoldedRevenueAccountNum);
        var cost = Changed(draft.HoldedExpenseAccountNum, current?.HoldedExpenseAccountNum);
        var teamUsage = Changed(draft.HoldedTransferAccountNum, current?.HoldedTransferAccountNum);
        return income is null && cost is null && teamUsage is null
            ? null
            : (await GetAccountChoicesAsync(ct)).Refusal(income, cost, teamUsage);
    }

    private static int? Changed(int? posted, int? stored) => posted == stored ? null : posted;

    // Income is the whole sales-and-income group (7xxxxxxx); store items normally sit in 759xxxxx.
    private const int IncomeBlockStart = 70_000_000;
    private const int IncomeBlockEnd = 79_999_999;
    private const int CostBlockStart = 62_900_000;
    private const int CostBlockEnd = 62_909_999;
    private const int TeamUsageBlockStart = 62_990_000;
    private const int TeamUsageBlockEnd = 62_999_999;

    internal static ProductDto MapProduct(Product p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive, p.HoldedRevenueAccountNum,
            p.HoldedExpenseAccountNum, p.HoldedTransferAccountNum);
}
