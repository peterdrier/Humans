using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models.Store;

public sealed class StoreSummaryViewModel
{
    public required StoreSummaryDto Summary { get; init; }
}
