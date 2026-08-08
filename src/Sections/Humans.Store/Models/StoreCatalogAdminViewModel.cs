using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

public sealed class StoreCatalogAdminViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Products { get; init; } = [];
}
