using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Feedback.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Feedback;

/// <summary>AdminOnly: other admin-shaped roles reach /Admin but must not see feedback counts.</summary>
internal sealed class SectionAdminTiles : ISectionAdminTiles
{
    public IEnumerable<AdminTile> Tiles() =>
    [
        new AdminTile("feedback.open", "Open feedback", "fa-solid fa-comment-dots", OpenAsync,
            Policy: PolicyNames.AdminOnly, Weight: 50)
    ];

    private static async ValueTask<AdminTileValue?> OpenAsync(IServiceProvider sp, CancellationToken ct)
    {
        var count = await sp.GetRequiredService<FeedbackService>().GetActionableCountAsync(ct);
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} open feedback");
    }
}
