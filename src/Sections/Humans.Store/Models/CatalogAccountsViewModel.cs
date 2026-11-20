using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

internal sealed class CatalogAccountsViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<CatalogAccountsRow> Rows { get; init; } = [];
    public ProductAccountChoices Choices { get; init; } = new([], [], []);
    public int MissingIncomeCount => Rows.Count(r => r.Income is null);
}

/// <summary><paramref name="Index"/> is the row's position in the posted list.</summary>
internal sealed record CatalogAccountsRow(
    int Index, ProductDto Product, int? Income, int? Cost, int? TeamUsage);

/// <summary>One posted row of the bulk accounts table.</summary>
internal sealed class CatalogAccountsInput
{
    public Guid ProductId { get; set; }
    public int? Income { get; set; }
    public int? Cost { get; set; }
    public int? TeamUsage { get; set; }
}
