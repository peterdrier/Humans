using System.ComponentModel.DataAnnotations;
using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models;

public sealed class StoreIndexViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public IReadOnlyList<StoreCampSeasonOrders> CampSeasons { get; init; } = [];
}

public sealed record StoreCampSeasonOrders(
    Guid CampSeasonId,
    string CampName,
    int Year,
    IReadOnlyList<OrderDto> Orders);

public sealed class StoreOrderViewModel
{
    public OrderDto Order { get; init; } = null!;
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public string CampName { get; init; } = string.Empty;
    public bool CanEdit { get; init; }
}

public sealed class StoreCatalogAdminViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Products { get; init; } = [];
}

public sealed class ProductInputModel
{
    public Guid? Id { get; set; }

    [Required]
    [Range(2000, 9999)]
    public int Year { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    [Range(0.0, 1_000_000.0)]
    public decimal UnitPriceEur { get; set; }

    [Range(0.0, 100.0)]
    public decimal VatRatePercent { get; set; }

    [Range(0.0, 1_000_000.0)]
    public decimal? DepositAmountEur { get; set; }

    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Use YYYY-MM-DD format")]
    public string OrderableUntil { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
