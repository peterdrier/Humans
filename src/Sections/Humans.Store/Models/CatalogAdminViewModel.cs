using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

internal sealed class CatalogAdminViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Products { get; init; } = [];
}
