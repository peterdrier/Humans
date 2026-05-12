using System.Text.Json;
using Humans.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketTransferTimelineViewComponent : ViewComponent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IViewComponentResult Invoke(TicketTransferRowDto request, string vendorStepsJson)
    {
        var steps = JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(
            vendorStepsJson, JsonOptions) ?? new();
        return View(new TicketTransferTimelineViewModel(request, steps));
    }
}

public sealed record TicketTransferTimelineViewModel(
    TicketTransferRowDto Request,
    IReadOnlyList<TicketTransferVendorStep> Steps);
