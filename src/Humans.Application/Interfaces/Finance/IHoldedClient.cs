using Humans.Application.DTOs.Finance;

namespace Humans.Application.Interfaces.Finance;

/// <summary>
/// Vendor connector for the Holded API. Lives in the Application layer as
/// an interface; implementation in Humans.Infrastructure.
/// </summary>
public interface IHoldedClient
{
    /// <summary>
    /// Fetch all purchase documents (full pull, paginated under the hood). Returns
    /// the raw wire DTOs alongside the original JSON string for each doc so the
    /// sync service can persist RawPayload verbatim.
    /// </summary>
    Task<IReadOnlyList<(HoldedDocDto Dto, string RawJson)>> GetAllPurchaseDocsAsync(CancellationToken ct = default);

    /// <summary>
    /// Push a tag onto a Holded purchase doc. Adds <paramref name="tag"/> to the
    /// existing tags array and PUTs the doc back. Returns true on success;
    /// returns false if the API rejects the request (e.g. tag-update unsupported).
    /// </summary>
    Task<bool> TryAddTagAsync(string holdedDocId, string tag, CancellationToken ct = default);
}
