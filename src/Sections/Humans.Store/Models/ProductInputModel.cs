using System.ComponentModel.DataAnnotations;
using Humans.Base.Attributes;

namespace Humans.Store.Models;

internal sealed class ProductInputModel
{
    public Guid? Id { get; set; }

    [Required]
    [Range(2000, 9999)]
    public int Year { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    [MarkdownContent]
    public string Description { get; set; } = string.Empty;

    [Range(0.0, 1_000_000.0)]
    public decimal UnitPriceEur { get; set; }

    [Range(0.0, 100.0)]
    public decimal VatRatePercent { get; set; }

    [Range(0.0, 1_000_000.0)]
    public decimal? DepositAmountEur { get; set; }

    /// <summary>Holded chart number this item's external revenue books to, from Acountax.</summary>
    [Range(10_000_000, 99_999_999, ErrorMessage = "Use the 8-digit Holded chart number, e.g. 75900002")]
    public int? HoldedRevenueAccountNum { get; set; }

    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Use YYYY-MM-DD format")]
    public string OrderableUntil { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
