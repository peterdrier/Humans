namespace Humans.Application.Interfaces.Holded;

public interface IHoldedClient
{
    /// <summary>Creates a purchase document and returns the new doc id.</summary>
    Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input,
        CancellationToken ct = default);

    /// <summary>Replaces the tags on an existing purchase document.</summary>
    Task UpdatePurchaseDocumentTagsAsync(
        string documentId,
        IReadOnlyList<string> tags,
        CancellationToken ct = default);

    /// <summary>Uploads a single attachment to a purchase document.</summary>
    Task UploadAttachmentAsync(
        string documentId,
        HoldedAttachmentInput attachment,
        CancellationToken ct = default);

    /// <summary>Reads a purchase document by id.</summary>
    Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId,
        CancellationToken ct = default);
}
