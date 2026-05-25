using System.ComponentModel.DataAnnotations;
using Humans.Domain.Attributes;

namespace Humans.Web.Models;

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
    [MarkdownContent]
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
