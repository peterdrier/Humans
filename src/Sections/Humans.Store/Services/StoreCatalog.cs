using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services.Dtos;
using NodaTime;
using NodaTime.Text;

namespace Humans.Store.Services;

internal sealed class StoreCatalog(
    IStoreRepository repo,
    IAuditLogService audit,
    IClock clock,
    ILogger<Service> logger) : IApplicationService
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
        var refusal = GetProductDraftRefusal(draft);
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
            StoreAuditActions.StoreProductCreated, AuditEntityTypes.Product, product.Id,
            $"Created store product '{product.Name}' for year {product.Year}",
            actorUserId);
        return CatalogSaveResult.Success(created: true);
    }

    public async Task<AdminMutationResult> UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        var refusal = GetProductDraftRefusal(draft);
        if (refusal is not null)
            return RefuseCatalogMutation(draft.Id, actorUserId, refusal);

        var product = await repo.GetProductByIdAsync(draft.Id, ct);
        if (product is null)
            return RefuseCatalogMutation(draft.Id, actorUserId, $"Product {draft.Id} not found");

        var oldPrice = product.UnitPriceEur;

        ApplyProductFields(product, draft);
        product.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateProductAsync(product, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreProductUpdated, AuditEntityTypes.Product, product.Id,
            $"Updated store product '{product.Name}'",
            actorUserId);

        // A dedicated, queryable price-change event for the order page's audit view.
        if (oldPrice != draft.UnitPriceEur)
            await audit.LogAsync(
                StoreAuditActions.StoreProductPriceChanged, AuditEntityTypes.Product, product.Id,
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
            request.HoldedRevenueAccountNum);

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
            StoreAuditActions.StoreProductDeactivated, AuditEntityTypes.Product, productId,
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
        if (draft.HoldedRevenueAccountNum is < 10_000_000 or > 99_999_999)
            return "Holded revenue account must be an 8-digit chart number";
        return null;
    }

    internal static ProductDto MapProduct(Product p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive, p.HoldedRevenueAccountNum);
}
