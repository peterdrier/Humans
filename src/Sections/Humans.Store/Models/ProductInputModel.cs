using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Humans.Store.Services.Dtos;
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

    [BindRequired]
    [Range(0.0, 1_000_000.0)]
    [ModelBinder(BinderType = typeof(HtmlDecimalBinder))]
    public decimal UnitPriceEur { get; set; }

    [BindRequired]
    [Range(0.0, 100.0)]
    [ModelBinder(BinderType = typeof(HtmlDecimalBinder))]
    public decimal VatRatePercent { get; set; }

    [Range(0.0, 1_000_000.0)]
    [ModelBinder(BinderType = typeof(HtmlDecimalBinder))]
    public decimal? DepositAmountEur { get; set; }

    public int? HoldedRevenueAccountNum { get; set; }

    public int? HoldedExpenseAccountNum { get; set; }

    public int? HoldedTransferAccountNum { get; set; }

    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Use YYYY-MM-DD format")]
    public string OrderableUntil { get; set; } = string.Empty;

    [BindRequired]
    public bool IsActive { get; set; } = true;

    [BindNever]
    [ValidateNever]
    public ProductAccountChoices AccountChoices { get; set; } = new([], [], []);
}
