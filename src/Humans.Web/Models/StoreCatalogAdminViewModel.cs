using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models;

public sealed class StoreCatalogAdminViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Products { get; init; } = [];
}
